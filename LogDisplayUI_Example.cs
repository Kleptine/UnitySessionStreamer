using UnityEngine;
using Core.Streaming;

/// <summary>
/// Example demonstrating how to integrate the LogDisplayUI component into a Unity project.
/// </summary>
public class LogDisplayUIExample : MonoBehaviour
{
    void Start()
    {
        // Option 1: Add LogDisplayUI to a persistent GameObject
        GameObject logUIObject = new GameObject("LogDisplayUI");
        DontDestroyOnLoad(logUIObject);

        LogDisplayUI logUI = logUIObject.AddComponent<LogDisplayUI>();

        // The UI will capture all logs automatically once enabled
        // User can toggle it with the backtick (`) key by default

        // Generate some test logs to demonstrate
        Debug.Log("Application started successfully");
        Debug.LogWarning("This is a warning message");
        Debug.LogError("This is an error message");
    }

    // Option 2: Integrate with SessionStreamer
    void StartStreamingWithLogUI()
    {
        // Start the session streamer as usual
        SessionStreamer.StartStreamingSession(
            "https://stream.yourserver.com",
            "your_project_id",
            "unique_session_id",
            showRecordingIcon: true
        );

        // Add LogDisplayUI for in-game log viewing and downloading
        GameObject logUIObject = new GameObject("LogDisplayUI");
        DontDestroyOnLoad(logUIObject);
        logUIObject.AddComponent<LogDisplayUI>();
    }

    // Option 3: Programmatic control
    void ProgrammaticControl()
    {
        GameObject logUIObject = new GameObject("LogDisplayUI");
        DontDestroyOnLoad(logUIObject);

        LogDisplayUI logUI = logUIObject.AddComponent<LogDisplayUI>();

        // Show the UI programmatically
        logUI.Show();

        // Or hide it
        logUI.Hide();

        // Or toggle it
        logUI.Toggle();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Core.Streaming
{
    /// <summary>
    /// In-game UI component that displays Unity logs and provides a download button to save them to a file.
    /// </summary>
    public class LogDisplayUI : MonoBehaviour
    {
        [Header("UI Settings")]
        [SerializeField] private KeyCode toggleKey = KeyCode.BackQuote; // ` key
        [SerializeField] private int maxLogEntries = 1000;
        [SerializeField] private bool showOnStart = false;

        private bool showUI = false;
        private Vector2 scrollPosition = Vector2.zero;
        private readonly List<LogEntry> logEntries = new List<LogEntry>();
        private readonly StringBuilder displayText = new StringBuilder();
        private GUIStyle logBoxStyle;
        private GUIStyle buttonStyle;
        private GUIStyle windowStyle;
        private bool stylesInitialized = false;

        private struct LogEntry
        {
            public string message;
            public string stackTrace;
            public LogType type;
            public DateTime timestamp;

            public LogEntry(string message, string stackTrace, LogType type)
            {
                this.message = message;
                this.stackTrace = stackTrace;
                this.type = type;
                this.timestamp = DateTime.Now;
            }

            public string GetFormattedMessage()
            {
                string prefix = type switch
                {
                    LogType.Error => "[ERROR]",
                    LogType.Assert => "[ASSERT]",
                    LogType.Warning => "[WARNING]",
                    LogType.Log => "[LOG]",
                    LogType.Exception => "[EXCEPTION]",
                    _ => "[UNKNOWN]"
                };

                return $"{timestamp:HH:mm:ss.fff} {prefix} {message}";
            }

            public string GetFullMessage()
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"[{timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{type}]");
                sb.AppendLine(message);
                if (!string.IsNullOrEmpty(stackTrace))
                {
                    sb.AppendLine("Stack Trace:");
                    sb.AppendLine(stackTrace);
                }
                sb.AppendLine("---");
                return sb.ToString();
            }
        }

        private void OnEnable()
        {
            Application.logMessageReceived += HandleLog;
            showUI = showOnStart;
        }

        private void OnDisable()
        {
            Application.logMessageReceived -= HandleLog;
        }

        private void HandleLog(string logString, string stackTrace, LogType type)
        {
            lock (logEntries)
            {
                logEntries.Add(new LogEntry(logString, stackTrace, type));

                // Trim old entries if we exceed the maximum
                if (logEntries.Count > maxLogEntries)
                {
                    logEntries.RemoveAt(0);
                }
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey))
            {
                showUI = !showUI;
            }
        }

        private void InitializeStyles()
        {
            if (stylesInitialized) return;

            logBoxStyle = new GUIStyle(GUI.skin.textArea)
            {
                richText = true,
                wordWrap = true,
                fontSize = 12,
                padding = new RectOffset(10, 10, 10, 10)
            };

            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(10, 10, 5, 5)
            };

            windowStyle = new GUIStyle(GUI.skin.window)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold
            };

            stylesInitialized = true;
        }

        private void OnGUI()
        {
            if (!showUI) return;

            InitializeStyles();

            float windowWidth = Screen.width * 0.8f;
            float windowHeight = Screen.height * 0.7f;
            float windowX = (Screen.width - windowWidth) / 2;
            float windowY = (Screen.height - windowHeight) / 2;

            Rect windowRect = new Rect(windowX, windowY, windowWidth, windowHeight);
            GUI.Window(0, windowRect, DrawLogWindow, "Unity Log Viewer", windowStyle);
        }

        private void DrawLogWindow(int windowID)
        {
            GUILayout.BeginVertical();

            // Header with buttons
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Logs ({logEntries.Count}/{maxLogEntries})", GUILayout.Width(150));
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Download Logs", buttonStyle, GUILayout.Width(150), GUILayout.Height(30)))
            {
                DownloadLogs();
            }

            if (GUILayout.Button("Clear", buttonStyle, GUILayout.Width(100), GUILayout.Height(30)))
            {
                ClearLogs();
            }

            if (GUILayout.Button("Close", buttonStyle, GUILayout.Width(100), GUILayout.Height(30)))
            {
                showUI = false;
            }

            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Build display text from log entries
            BuildDisplayText();

            // Scrollable log area
            scrollPosition = GUILayout.BeginScrollView(scrollPosition, logBoxStyle);
            GUILayout.TextArea(displayText.ToString(), logBoxStyle, GUILayout.ExpandHeight(true));
            GUILayout.EndScrollView();

            GUILayout.EndVertical();

            // Make window draggable
            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        private void BuildDisplayText()
        {
            displayText.Clear();

            lock (logEntries)
            {
                foreach (var entry in logEntries)
                {
                    string coloredMessage = entry.type switch
                    {
                        LogType.Error => $"<color=red>{entry.GetFormattedMessage()}</color>",
                        LogType.Assert => $"<color=red>{entry.GetFormattedMessage()}</color>",
                        LogType.Warning => $"<color=yellow>{entry.GetFormattedMessage()}</color>",
                        LogType.Exception => $"<color=red>{entry.GetFormattedMessage()}</color>",
                        _ => $"<color=white>{entry.GetFormattedMessage()}</color>"
                    };

                    displayText.AppendLine(coloredMessage);
                }
            }
        }

        private void DownloadLogs()
        {
            try
            {
                // Create logs directory in persistent data path
                string logsDirectory = Path.Combine(Application.persistentDataPath, "ExportedLogs");
                if (!Directory.Exists(logsDirectory))
                {
                    Directory.CreateDirectory(logsDirectory);
                }

                // Generate filename with timestamp
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string filename = $"UnityLog_{timestamp}.txt";
                string filepath = Path.Combine(logsDirectory, filename);

                // Build the full log content
                StringBuilder logContent = new StringBuilder();
                logContent.AppendLine("=== Unity Log Export ===");
                logContent.AppendLine($"Export Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                logContent.AppendLine($"Application: {Application.productName}");
                logContent.AppendLine($"Version: {Application.version}");
                logContent.AppendLine($"Unity Version: {Application.unityVersion}");
                logContent.AppendLine($"Platform: {Application.platform}");
                logContent.AppendLine($"Log Entries: {logEntries.Count}");
                logContent.AppendLine("========================");
                logContent.AppendLine();

                lock (logEntries)
                {
                    foreach (var entry in logEntries)
                    {
                        logContent.Append(entry.GetFullMessage());
                    }
                }

                // Write to file
                File.WriteAllText(filepath, logContent.ToString());

                Debug.Log($"(LogDisplayUI) Logs downloaded successfully to: {filepath}");

                // Also try to open the directory (platform dependent)
                TryOpenDirectory(logsDirectory);
            }
            catch (Exception e)
            {
                Debug.LogError($"(LogDisplayUI) Failed to download logs: {e.Message}");
            }
        }

        private void TryOpenDirectory(string path)
        {
            try
            {
                #if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                System.Diagnostics.Process.Start("explorer.exe", path);
                #elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
                System.Diagnostics.Process.Start("open", path);
                #elif UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
                System.Diagnostics.Process.Start("xdg-open", path);
                #endif
            }
            catch
            {
                // Silently fail if we can't open the directory
            }
        }

        private void ClearLogs()
        {
            lock (logEntries)
            {
                logEntries.Clear();
            }
            scrollPosition = Vector2.zero;
            Debug.Log("(LogDisplayUI) Logs cleared.");
        }

        /// <summary>
        /// Programmatically show the log UI.
        /// </summary>
        public void Show()
        {
            showUI = true;
        }

        /// <summary>
        /// Programmatically hide the log UI.
        /// </summary>
        public void Hide()
        {
            showUI = false;
        }

        /// <summary>
        /// Toggle the log UI visibility.
        /// </summary>
        public void Toggle()
        {
            showUI = !showUI;
        }
    }
}

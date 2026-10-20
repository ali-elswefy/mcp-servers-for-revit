using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RevitMcp.Interop
{
    /// <summary>One window that is blocking Revit, as far as Win32 can describe it.</summary>
    public class BlockingDialog
    {
        public string Title { get; set; }
        public string ClassName { get; set; }

        /// <summary>Static text of a classic Win32 dialog. Empty for task dialogs and WPF windows, whose text Win32 cannot read.</summary>
        public string Text { get; set; }

        /// <summary>Button captions of a classic Win32 dialog. Empty for task dialogs and WPF windows.</summary>
        public List<string> Buttons { get; set; } = new List<string>();

        public Dictionary<string, object> ToData()
        {
            return new Dictionary<string, object>
            {
                ["title"] = Title,
                ["className"] = ClassName,
                ["text"] = string.IsNullOrEmpty(Text) ? null : Text,
                ["buttons"] = Buttons.Count == 0 ? null : Buttons
            };
        }
    }

    public class RevitUiState
    {
        /// <summary>False when Revit's main window is unknown, so nothing could be checked.</summary>
        public bool Checked { get; set; }

        /// <summary>A modal window has disabled Revit's main window. Revit cannot run API requests until the user closes it.</summary>
        public bool ModalDialogOpen { get; set; }

        public List<BlockingDialog> Dialogs { get; set; } = new List<BlockingDialog>();

        /// <summary>"Title" or "Title (and 1 more)", for messages.</summary>
        public string DescribeDialogs()
        {
            if (Dialogs.Count == 0)
                return "an untitled dialog";

            string first = string.IsNullOrEmpty(Dialogs[0].Title) ? "an untitled dialog" : "'" + Dialogs[0].Title + "'";
            return Dialogs.Count == 1 ? first : first + " (and " + (Dialogs.Count - 1) + " more)";
        }

        public List<Dictionary<string, object>> DialogsData()
        {
            var list = new List<Dictionary<string, object>>();
            foreach (var dialog in Dialogs)
                list.Add(dialog.ToData());
            return list;
        }
    }

    /// <summary>
    /// Detects whether Revit is blocked by a modal window (a failure dialog, a task dialog, an
    /// options window, another add-in's dialog). While one is open Revit raises no Idling events,
    /// so external events, and therefore every MCP command, cannot run until the user closes it.
    ///
    /// Uses Win32 only, never the Revit API, so it can be called from any thread. It never sends
    /// a blocking message to the UI thread: titles are read with InternalGetWindowText and child
    /// text with SendMessageTimeout, so a busy UI thread cannot hang the caller.
    ///
    /// This file is compiled into both the plugin and the command set; the main window handle is
    /// shared through AppDomain data (set by the plugin, which owns the Revit UI application).
    /// </summary>
    public static class RevitDialogDetector
    {
        /// <summary>The <c>kind</c> on JSON-RPC errors returned when a dialog blocks Revit.</summary>
        public const string ErrorKind = "user_input_required";
        public const string ErrorReason = "modal_dialog_open";

        public const string MainWindowDataKey = "RevitMcp.MainWindowHandle";

        private const int MaxDialogs = 5;
        private const int MaxTextLength = 1000;
        private const int ChildTextTimeoutMs = 200;

        public static void SetMainWindow(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
                AppDomain.CurrentDomain.SetData(MainWindowDataKey, handle.ToInt64());
        }

        public static IntPtr MainWindow
        {
            get
            {
                object stored = AppDomain.CurrentDomain.GetData(MainWindowDataKey);
                if (stored is long value && value != 0)
                    return new IntPtr(value);

                try
                {
                    using (var process = Process.GetCurrentProcess())
                        return process.MainWindowHandle;
                }
                catch (Exception)
                {
                    return IntPtr.Zero;
                }
            }
        }

        public static RevitUiState Detect()
        {
            var state = new RevitUiState();
            IntPtr main = MainWindow;
            if (main == IntPtr.Zero || !IsWindow(main))
                return state;

            state.Checked = true;

            // A modal window (MFC, WinForms, WPF or a task dialog with Revit as its owner) disables its owner.
            if (IsWindowEnabled(main))
                return state;

            state.ModalDialogOpen = true;

            uint uiThreadId = GetWindowThreadProcessId(main, out _);
            var dialogs = state.Dialogs;
            EnumThreadWindows(uiThreadId, (hwnd, _) =>
            {
                // The modal window is the one still enabled; the windows it blocks are disabled with Revit's main window.
                if (hwnd == main || !IsWindowVisible(hwnd) || !IsWindowEnabled(hwnd))
                    return true;

                string className = GetClassNameOf(hwnd);
                long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
                bool isDialog = className == "#32770" || (style & WS_CAPTION) == WS_CAPTION;
                if (!isDialog)
                    return true; // Tooltips, drop shadows, menus.

                dialogs.Add(Describe(hwnd, className));
                return dialogs.Count < MaxDialogs;
            }, IntPtr.Zero);

            return state;
        }

        private static BlockingDialog Describe(IntPtr hwnd, string className)
        {
            var dialog = new BlockingDialog { Title = GetTitle(hwnd), ClassName = className };
            if (className != "#32770")
                return dialog;

            var text = new StringBuilder();
            EnumChildWindows(hwnd, (child, _) =>
            {
                if (!IsWindowVisible(child))
                    return true;

                string childClass = GetClassNameOf(child);
                if (childClass == "Static" || childClass == "Button")
                {
                    string value = GetChildText(child);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        if (childClass == "Button")
                        {
                            dialog.Buttons.Add(value.Replace("&", string.Empty));
                        }
                        else if (text.Length < MaxTextLength)
                        {
                            if (text.Length > 0) text.Append('\n');
                            text.Append(value.Trim());
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);

            dialog.Text = text.Length > MaxTextLength ? text.ToString(0, MaxTextLength) + "..." : text.ToString();
            return dialog;
        }

        private static string GetTitle(IntPtr hwnd)
        {
            var buffer = new StringBuilder(512);
            InternalGetWindowText(hwnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        private static string GetClassNameOf(IntPtr hwnd)
        {
            var buffer = new StringBuilder(256);
            GetClassName(hwnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        private static string GetChildText(IntPtr hwnd)
        {
            var buffer = new StringBuilder(MaxTextLength + 1);
            IntPtr ok = SendMessageTimeout(hwnd, WM_GETTEXT, new IntPtr(buffer.Capacity), buffer,
                SMTO_ABORTIFHUNG | SMTO_BLOCK, ChildTextTimeoutMs, out _);
            return ok == IntPtr.Zero ? string.Empty : buffer.ToString();
        }

        // ---- Win32 ----

        private const int GWL_STYLE = -16;
        private const long WS_CAPTION = 0x00C00000L;
        private const uint WM_GETTEXT = 0x000D;
        private const uint SMTO_BLOCK = 0x0001;
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int InternalGetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, StringBuilder lParam,
            uint flags, uint timeoutMs, out IntPtr result);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    }
}

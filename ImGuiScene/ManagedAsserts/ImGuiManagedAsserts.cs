using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace ImGuiScene.ManagedAsserts
{
    public static class ImGuiManagedAsserts {
        public static bool EnableAsserts;

        /// <summary>
        /// Needs to be called while rendering an ImGui frame.
        /// </summary>
        public static unsafe ProblemSnapshot GetSnapshot()
        {
            var contextPtr = ImGui.GetCurrentContext();

            var styleVarStack = *((int *)contextPtr + ImGuiContextOffsets.StyleVarStackOffset); // ImVector.Size
            var colorStack = *((int *)contextPtr + ImGuiContextOffsets.ColorStackOffset); // ImVector.Size
            var fontStack = *((int *)contextPtr + ImGuiContextOffsets.FontStackOffset); // ImVector.Size
            var popupStack = *((int *)contextPtr + ImGuiContextOffsets.BeginPopupStackOffset); // ImVector.Size
            var windowStack = *((int *)contextPtr + ImGuiContextOffsets.CurrentWindowStackOffset); // ImVector.Size

            return new ProblemSnapshot
            {
                StyleVarStackSize = styleVarStack,
                ColorStackSize = colorStack,
                FontStackSize = fontStack,
                BeginPopupStackSize = popupStack,
                WindowStackSize = windowStack,
            };
        }

        public static void ReportProblems(ProblemSnapshot before)
        {
            if (!EnableAsserts)
                return;

            var cSnap = GetSnapshot();

            if (before.StyleVarStackSize != cSnap.StyleVarStackSize)
            {
                ShowAssert($"You forgot to pop a style var!\n\nBefore: {before.StyleVarStackSize}, after: {cSnap.StyleVarStackSize}");
                return;
            }

            if (before.ColorStackSize != cSnap.ColorStackSize)
            {
                ShowAssert($"You forgot to pop a color!\n\nBefore: {before.ColorStackSize}, after: {cSnap.ColorStackSize}");
                return;
            }

            if (before.FontStackSize != cSnap.FontStackSize)
            {
                ShowAssert($"You forgot to pop a font!\n\nBefore: {before.FontStackSize}, after: {cSnap.FontStackSize}");
                return;
            }

            if (before.BeginPopupStackSize != cSnap.BeginPopupStackSize)
            {
                ShowAssert($"You forgot to end a popup!\n\nBefore: {before.BeginPopupStackSize}, after: {cSnap.BeginPopupStackSize}");
                return;
            }

            if (cSnap.WindowStackSize != 1)
            {
                if (cSnap.WindowStackSize > 1)
                {
                    ShowAssert($"Mismatched Begin/BeginChild vs End/EndChild calls: did you forget to call End/EndChild?\n\ncSnap.WindowStackSize = {cSnap.WindowStackSize}");
                }
                else
                {
                    ShowAssert($"Mismatched Begin/BeginChild vs End/EndChild calls: did you call End/EndChild too much?\n\ncSnap.WindowStackSize = {cSnap.WindowStackSize}");
                }
            }
        }

        private enum MessageBoxType : uint
        {
            DefaultValue = 0x0,
            Ok = DefaultValue,
            IconStop = 0x10,
            IconError = IconStop,
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, MessageBoxType type);

        private static void ShowAssert(string message)
        {
            var flags = MessageBoxType.Ok | MessageBoxType.IconError;
            _ = MessageBoxW(Process.GetCurrentProcess().MainWindowHandle, message + "\n\nAsserts are now disabled. You may re-enable them.", "You fucked up", flags);

            EnableAsserts = false;
        }

        public class ProblemSnapshot
        {
            public int StyleVarStackSize { get; set; }
            public int ColorStackSize { get; set; }
            public int FontStackSize { get; set; }
            public int BeginPopupStackSize { get; set; }
            public int WindowStackSize { get; set; }
        }
    }
}
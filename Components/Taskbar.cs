using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WebMConverter.Components
{
    enum TaskbarProgressBarState
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8
    }

    /// <summary>
    /// Progress on the main window's taskbar button (ITaskbarList3). Replaces the Windows API Code Pack, which was two
    /// DLLs (640 KB) used for nothing else. Calls from other threads are forwarded to the owner's UI thread; if the
    /// taskbar is unavailable (no Explorer shell, for example) the calls do nothing.
    /// </summary>
    sealed class TaskbarManager
    {
        public static TaskbarManager Instance { get; } = new TaskbarManager();

        /// <summary>The window whose taskbar button shows the progress. Set once by MainForm.</summary>
        public Form Owner { get; set; }

        ITaskbarList3 _list;
        bool _unavailable;

        TaskbarManager() { }

        public void SetProgressState(TaskbarProgressBarState state) =>
            Run((list, hwnd) => list.SetProgressState(hwnd, state));

        public void SetProgressValue(int currentValue, int maximumValue) =>
            Run((list, hwnd) => list.SetProgressValue(hwnd, (ulong)Math.Max(0, currentValue), (ulong)Math.Max(0, maximumValue)));

        void Run(Action<ITaskbarList3, IntPtr> call)
        {
            var owner = Owner;
            if (owner == null || owner.IsDisposed || !owner.IsHandleCreated)
                return;

            if (owner.InvokeRequired)
            {
                try { owner.BeginInvoke((Action)(() => Run(call))); }
                catch (InvalidOperationException) { } // closing
                return;
            }

            if (_unavailable)
                return;

            try
            {
                if (_list == null)
                {
                    var list = (ITaskbarList3)new CTaskbarList();
                    list.HrInit();
                    _list = list;
                }
                call(_list, owner.Handle);
            }
            catch (Exception e) when (e is COMException || e is InvalidCastException)
            {
                _unavailable = true;
            }
        }

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
        class CTaskbarList { }

        // Only the methods up to SetProgressState are declared; the vtable order must match the native interface.
        [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITaskbarList3
        {
            // ITaskbarList
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            // ITaskbarList2
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
            // ITaskbarList3
            void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
            void SetProgressState(IntPtr hwnd, TaskbarProgressBarState state);
        }
    }
}

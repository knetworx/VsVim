using System;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;
using System.Threading;

namespace Vim.UI.Wpf.Implementation.Misc
{
    [Export(typeof(IClipboardDevice))]
    internal sealed class ClipboardDevice : IClipboardDevice
    {
        private static class NativeMethods
        {
            public const uint CF_UNICODETEXT = 13;
            public const uint GMEM_MOVEABLE = 0x0002;

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool OpenClipboard(IntPtr hWndNewOwner);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseClipboard();

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool EmptyClipboard();

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr GetClipboardData(uint uFormat);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr GlobalLock(IntPtr hMem);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GlobalUnlock(IntPtr hMem);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr GlobalFree(IntPtr hMem);
        }

        private bool _reportErrors;

        [ImportingConstructor]
        internal ClipboardDevice(IProtectedOperations protectedOperations)
        {
            _reportErrors = false;
        }

        /// <summary>
        /// Read clipboard text via Win32 directly, retrying on contention.
        /// Runs on a background thread so Thread.Sleep doesn't block the UI.
        /// </summary>
        private static string GetText()
        {
            string result = string.Empty;
            var thread = new Thread(() => { result = Win32GetText(); });
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
            return result;
        }

        /// <summary>
        /// Write clipboard text via Win32 directly, fire-and-forget on a background
        /// thread. Skips Flush() entirely — no second OpenClipboard, no UI freeze.
        /// VsVim's internal register is the authoritative store; the system clipboard
        /// update doesn't need to complete synchronously.
        /// </summary>
        private static void SetText(string text)
        {
            var thread = new Thread(() => Win32SetText(text));
            thread.IsBackground = true;
            thread.Start();
        }

        private static string Win32GetText()
        {
            const int maxRetries = 5;
            for (int i = 0; i < maxRetries; i++)
            {
                if (NativeMethods.OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        IntPtr hData = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
                        if (hData == IntPtr.Zero)
                            return string.Empty;

                        IntPtr pData = NativeMethods.GlobalLock(hData);
                        if (pData == IntPtr.Zero)
                            return string.Empty;

                        try
                        {
                            return Marshal.PtrToStringUni(pData) ?? string.Empty;
                        }
                        finally
                        {
                            NativeMethods.GlobalUnlock(hData);
                        }
                    }
                    finally
                    {
                        NativeMethods.CloseClipboard();
                    }
                }

                if (i < maxRetries - 1)
                    Thread.Sleep(10);
            }

            return string.Empty;
        }

        private static void Win32SetText(string text)
        {
            const int maxRetries = 5;
            for (int i = 0; i < maxRetries; i++)
            {
                if (NativeMethods.OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        NativeMethods.EmptyClipboard();

                        int charCount = text.Length + 1;
                        IntPtr hMem = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)(charCount * 2));
                        if (hMem == IntPtr.Zero)
                            return;

                        IntPtr pMem = NativeMethods.GlobalLock(hMem);
                        if (pMem == IntPtr.Zero)
                        {
                            NativeMethods.GlobalFree(hMem);
                            return;
                        }

                        try
                        {
                            Marshal.Copy(text.ToCharArray(), 0, pMem, text.Length);
                            Marshal.WriteInt16(pMem, text.Length * 2, 0);
                        }
                        finally
                        {
                            NativeMethods.GlobalUnlock(hMem);
                        }

                        if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, hMem) != IntPtr.Zero)
                            return; // success; SetClipboardData owns hMem now

                        // SetClipboardData failed; we still own hMem
                        NativeMethods.GlobalFree(hMem);
                    }
                    finally
                    {
                        NativeMethods.CloseClipboard();
                    }
                }

                if (i < maxRetries - 1)
                    Thread.Sleep(10);
            }
        }

        #region IClipboardDevice

        bool IClipboardDevice.ReportErrors
        {
            get { return _reportErrors; }
            set { _reportErrors = value; }
        }

        string IClipboardDevice.Text
        {
            get { return GetText(); }
            set { SetText(value); }
        }

        #endregion
    }
}

using System.Runtime.InteropServices;

namespace SpoofGUI.Core;

public static class NativeFilePicker
{
    public static string? PickExecutableOrShortcut(IntPtr owner)
    {
        const int maxPathBuffer = 32768;
        var fileBuffer = Marshal.AllocHGlobal(maxPathBuffer * sizeof(char));
        var initialDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        try
        {
            Marshal.WriteInt16(fileBuffer, 0);

            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                hwndOwner = owner,
                lpstrFilter = "Apps and shortcuts (*.exe;*.lnk)\0*.exe;*.lnk\0Executables (*.exe)\0*.exe\0Shortcuts (*.lnk)\0*.lnk\0All files (*.*)\0*.*\0",
                lpstrFile = fileBuffer,
                nMaxFile = maxPathBuffer,
                lpstrInitialDir = initialDir,
                lpstrTitle = "Choose app or shortcut",
                Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY | OFN_NOCHANGEDIR,
                lpstrDefExt = "exe",
            };

            if (GetOpenFileName(ref ofn))
                return Marshal.PtrToStringUni(fileBuffer);

            var error = CommDlgExtendedError();
            if (error != 0)
                throw new InvalidOperationException($"file picker failed (0x{error:X})");

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuffer);
        }
    }

    private const int OFN_READONLY = 0x00000001;
    private const int OFN_HIDEREADONLY = 0x00000004;
    private const int OFN_NOCHANGEDIR = 0x00000008;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_EXPLORER = 0x00080000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OPENFILENAME ofn);

    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();
}

using System.Runtime.InteropServices;

namespace MuhabbetKusu;

internal static class AudioFilePicker
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int Size;
        public IntPtr Owner, Instance;
        public string Filter;
        public IntPtr CustomFilter;
        public int MaxCustomFilter, FilterIndex;
        public IntPtr File;
        public int MaxFile;
        public IntPtr FileTitle;
        public int MaxFileTitle;
        public string InitialDirectory, Title;
        public int Flags;
        public short FileOffset, ExtensionOffset;
        public IntPtr DefaultExtension, CustomData, Hook, TemplateName, Reserved;
        public int ReservedValue, FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OpenFileName dialog);

    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();

    public static string? Pick(IntPtr owner, string directory)
    {
        var buffer = Marshal.AllocHGlobal(32768 * sizeof(char));
        try
        {
            Marshal.WriteInt16(buffer, 0);
            var dialog = new OpenFileName
            {
            Size = Marshal.SizeOf<OpenFileName>(), Owner = owner,
            Filter = "Ses Dosyaları\0*.wav;*.mp3;*.flac;*.m4a;*.ogg;*.aac;*.wma\0\0",
            FilterIndex = 1, File = buffer, MaxFile = 32768,
            InitialDirectory = directory, Title = "Ses Dosyası Seç",
            Flags = 0x80000 | 0x1000 | 0x800 | 0x8
            };
            if (GetOpenFileNameW(ref dialog)) return Marshal.PtrToStringUni(buffer);
            int error = CommDlgExtendedError();
            if (error != 0) throw new InvalidOperationException($"Dosya seçim penceresi açılamadı (0x{error:X}).");
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}

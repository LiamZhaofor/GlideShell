using System.Runtime.InteropServices;

namespace GlideShell.Platform.Ui;

/// <summary>系统的「打开」文件对话框（comdlg32，模态，在调用线程上跑消息循环）。</summary>
public static unsafe partial class OpenFileDialog
{
    private const uint OFN_NOCHANGEDIR = 0x00000008, OFN_PATHMUSTEXIST = 0x00000800, OFN_FILEMUSTEXIST = 0x00001000,
        OFN_EXPLORER = 0x00080000, OFN_NODEREFERENCELINKS = 0x00100000, OFN_DONTADDTORECENT = 0x02000000;

    /// <summary>
    /// 选一个程序：exe、快捷方式（.lnk 原样返回，不换成它指向的文件，参数也保留）、批处理等。取消返回 null。
    /// </summary>
    /// <param name="filterName">「程序」这一类在文件类型下拉里的名字。</param>
    public static string? PickProgram(nint owner, string title, string filterName, string allFilesName)
    {
        // 过滤器：名字\0模式\0……\0\0
        string filter = $"{filterName}\0*.exe;*.lnk;*.bat;*.cmd;*.ps1;*.url;*.msc\0{allFilesName}\0*.*\0\0";
        const int max = 4096;
        char* buffer = stackalloc char[max];
        buffer[0] = '\0';
        fixed (char* f = filter)
        fixed (char* t = title)
        {
            var ofn = new OpenFileName
            {
                StructSize = (uint)sizeof(OpenFileName),
                Owner = owner,
                Filter = f,
                FilterIndex = 1,
                File = buffer,
                MaxFile = max,
                Title = t,
                Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_NODEREFERENCELINKS | OFN_DONTADDTORECENT,
            };
            if (!GetOpenFileNameW(&ofn)) return null;
            string path = new(buffer);
            return path.Length > 0 ? path : null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenFileName
    {
        public uint StructSize;
        public nint Owner, Instance;
        public char* Filter, CustomFilter;
        public uint MaxCustomFilter, FilterIndex;
        public char* File;
        public uint MaxFile;
        public char* FileTitle;
        public uint MaxFileTitle;
        public char* InitialDir, Title;
        public uint Flags;
        public ushort FileOffset, FileExtension;
        public char* DefExt;
        public nint CustData, Hook;
        public char* TemplateName;
        public void* Reserved;
        public uint ReservedDword, FlagsEx;
    }

    [LibraryImport("comdlg32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetOpenFileNameW(OpenFileName* ofn);
}

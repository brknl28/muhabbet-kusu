using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace MuhabbetKusu.Tests;

public class AudioFilePickerTests
{
    [Fact]
    public void NativeDialogStructureCanBeMarshaledWithUnicodeFileBuffer()
    {
        var type = typeof(AudioFilePicker).GetNestedType("OpenFileName", BindingFlags.NonPublic)!;
        var value = Activator.CreateInstance(type)!;
        var fileBuffer = Marshal.StringToHGlobalUni("Türkçe ses.wav");
        var native = Marshal.AllocHGlobal(Marshal.SizeOf(type));
        bool marshaled = false;
        try
        {
            type.GetField("File")!.SetValue(value, fileBuffer);
            type.GetField("Filter")!.SetValue(value, "Ses\0*.wav\0\0");
            Marshal.StructureToPtr(value, native, false);
            marshaled = true;
            var roundTrip = Marshal.PtrToStructure(native, type)!;
            var returnedBuffer = (IntPtr)type.GetField("File")!.GetValue(roundTrip)!;
            Assert.Equal("Türkçe ses.wav", Marshal.PtrToStringUni(returnedBuffer));
        }
        finally
        {
            if (marshaled) Marshal.DestroyStructure(native, type);
            Marshal.FreeHGlobal(native);
            Marshal.FreeHGlobal(fileBuffer);
        }
    }
}

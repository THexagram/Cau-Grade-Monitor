using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CauGradeMonitor.Desktop.Services;

internal static class SecretProtector
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CauGradeMonitor.Desktop/v1");

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var protectedBytes = Transform(Encoding.UTF8.GetBytes(value), true);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var clearBytes = Transform(Convert.FromBase64String(value), false);
        return Encoding.UTF8.GetString(clearBytes);
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputBlob = ToBlob(input);
        var entropyBlob = ToBlob(Entropy);
        var outputBlob = new DataBlob();
        IntPtr description = IntPtr.Zero;

        try
        {
            var ok = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob)
                : CryptUnprotectData(ref inputBlob, out description, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());

            var output = new byte[outputBlob.Length];
            Marshal.Copy(outputBlob.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            ZeroFree(inputBlob.Data, inputBlob.Length);
            ZeroFree(entropyBlob.Data, entropyBlob.Length);
            if (outputBlob.Data != IntPtr.Zero) LocalFree(outputBlob.Data);
            if (description != IntPtr.Zero) LocalFree(description);
        }
    }

    private static DataBlob ToBlob(byte[] data)
    {
        var pointer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        return new DataBlob { Length = data.Length, Data = pointer };
    }

    private static void ZeroFree(IntPtr pointer, int length)
    {
        if (pointer == IntPtr.Zero) return;
        for (var i = 0; i < length; i++) Marshal.WriteByte(pointer, i, 0);
        Marshal.FreeHGlobal(pointer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace EhGalleryDownloader;

public static class SecretStore
{
    private const int CryptprotectUiForbidden = 0x1;

    public static void SaveCookie(string cookie)
    {
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        var encrypted = Protect(Encoding.UTF8.GetBytes(cookie));
        File.WriteAllBytes(SettingsStore.EncryptedCookiePath, encrypted);
    }

    public static string? LoadCookie()
    {
        try
        {
            if (!File.Exists(SettingsStore.EncryptedCookiePath)) return null;
            var plain = Unprotect(File.ReadAllBytes(SettingsStore.EncryptedCookiePath));
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    public static void ClearCookie()
    {
        try { File.Delete(SettingsStore.EncryptedCookiePath); } catch { }
    }

    private static byte[] Protect(byte[] data)
    {
        var input = CreateBlob(data);
        var entropyBytes = Encoding.UTF8.GetBytes("EhGalleryDownloader.Cookie.v1");
        var entropy = CreateBlob(entropyBytes);
        DataBlob output = default;
        try
        {
            if (!CryptProtectData(ref input, "EhGalleryDownloader Cookie", ref entropy,
                    IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out output))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return CopyBlob(output);
        }
        finally
        {
            FreeInputBlob(input, clear: true);
            FreeInputBlob(entropy, clear: false);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }

    private static byte[] Unprotect(byte[] data)
    {
        var input = CreateBlob(data);
        var entropyBytes = Encoding.UTF8.GetBytes("EhGalleryDownloader.Cookie.v1");
        var entropy = CreateBlob(entropyBytes);
        DataBlob output = default;
        IntPtr description = IntPtr.Zero;
        try
        {
            if (!CryptUnprotectData(ref input, out description, ref entropy,
                    IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out output))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return CopyBlob(output);
        }
        finally
        {
            FreeInputBlob(input, clear: false);
            FreeInputBlob(entropy, clear: false);
            if (description != IntPtr.Zero) LocalFree(description);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { cbData = bytes.Length, pbData = pointer };
    }

    private static byte[] CopyBlob(DataBlob blob)
    {
        var bytes = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, bytes, 0, bytes.Length);
        return bytes;
    }

    private static void FreeInputBlob(DataBlob blob, bool clear)
    {
        if (blob.pbData == IntPtr.Zero) return;
        if (clear)
        {
            for (var i = 0; i < blob.cbData; i++)
                Marshal.WriteByte(blob.pbData, i, 0);
        }
        Marshal.FreeHGlobal(blob.pbData);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string szDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        out IntPtr ppszDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}

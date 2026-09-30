using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FeatherBrowser.Core;

/// <summary>
/// Windows 数据保护 API（DPAPI）的薄封装。
///
/// <p>用「当前用户 + 额外熵」作用域：密文只有同一个 Windows 用户在**同一台机器**上能解开，
/// 把整个文件拷走也没用。加熵是为了让别的程序即使拿到 DPAPI 解密能力，
/// 也不知道要用什么额外参数，不能直接把我们的密文解开。
/// </summary>
internal static class Dpapi
{
    /// <summary>本程序专用的附加熵。换掉它会导致已保存的密文全部解不开。</summary>
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("FeatherBrowser.CredentialStore.v1");

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    /// <summary>加密（使用本程序自己的附加熵）。</summary>
    public static byte[] Protect(byte[] plain) => Protect(plain, Entropy);

    /// <summary>解密（使用本程序自己的附加熵）。失败返回 null。</summary>
    public static byte[] Unprotect(byte[] cipher) => Unprotect(cipher, Entropy);

    /// <summary>
    /// 指定附加熵的加密。
    ///
    /// <p>需要这个重载是因为**别的程序有自己的约定**：Chromium 系浏览器（含夸克）
    /// 保存主密钥时用的是「无附加熵」的 DPAPI，解它必须传 null，
    /// 传了我们自己的熵会直接失败（错误码 13 = 数据无效）。
    /// </summary>
    public static byte[] Protect(byte[] plain, byte[] entropy) =>
        Transform(plain, entropy, protect: true);

    /// <summary>指定附加熵的解密。失败返回 null。</summary>
    public static byte[] Unprotect(byte[] cipher, byte[] entropy)
    {
        try
        {
            return Transform(cipher, entropy, protect: false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解密，失败时把 Win32 错误码一并抛出来，便于区分「熵不对」和「换机器了」。</summary>
    public static bool TryUnprotect(byte[] cipher, byte[] entropy, out byte[] plain,
        out int errorCode)
    {
        try
        {
            plain = Transform(cipher, entropy, protect: false);
            errorCode = 0;
            return true;
        }
        catch (Exception ex)
        {
            plain = null;
            errorCode = ex.HResult;
            return false;
        }
    }

    private static byte[] Transform(byte[] input, byte[] entropy, bool protect)
    {
        if (input == null || input.Length == 0)
        {
            return Array.Empty<byte>();
        }

        byte[] entropyBytes = entropy ?? Array.Empty<byte>();

        var inBlob = new DATA_BLOB();
        var entropyBlob = new DATA_BLOB();
        DATA_BLOB outBlob;

        IntPtr inPtr = Marshal.AllocHGlobal(input.Length);
        IntPtr entropyPtr = entropyBytes.Length > 0
            ? Marshal.AllocHGlobal(entropyBytes.Length)
            : IntPtr.Zero;
        try
        {
            Marshal.Copy(input, 0, inPtr, input.Length);
            inBlob.cbData = input.Length;
            inBlob.pbData = inPtr;

            if (entropyPtr != IntPtr.Zero)
            {
                Marshal.Copy(entropyBytes, 0, entropyPtr, entropyBytes.Length);
                entropyBlob.cbData = entropyBytes.Length;
                entropyBlob.pbData = entropyPtr;
            }

            // CRYPTPROTECT_UI_FORBIDDEN：不弹任何系统对话框，失败就直接返回失败
            const uint UiForbidden = 0x1;

            bool ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    UiForbidden, out outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero,
                    IntPtr.Zero, UiForbidden, out outBlob);

            if (!ok)
            {
                throw new CryptographicException(
                    "DPAPI 调用失败，错误码 " + Marshal.GetLastWin32Error(),
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }

            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            // 明文缓冲区用完立刻清零，减少留在内存里的时间
            if (protect)
            {
                for (int i = 0; i < input.Length; i++)
                {
                    Marshal.WriteByte(inPtr, i, 0);
                }
            }
            Marshal.FreeHGlobal(inPtr);
            if (entropyPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(entropyPtr);
            }
        }
    }
}

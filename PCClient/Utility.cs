using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;

static class Utility
{
    // ── 启动组注册 ────────────────────────────────────────────────────
    public static void EnsureInStartup()
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return;

        try
        {
            const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            if (key == null) return;

            string valueName = $"{Path.GetFileNameWithoutExtension(exePath)}_{MD5(exePath)}";
            key.SetValue(valueName, exePath);
        }
        catch { }
    }


    public static string MD5(string input)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant(); // 32 hex chars
    }

    // 计算文件的 MD5（用于更新校验）
    public static string Md5File(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        var bytes = System.Security.Cryptography.MD5.HashData(fs);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static byte[]? AESEncrypt(byte[] plain, string key, string iv)
    {
        try
        {
            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(key)[..32];
            aes.IV = Encoding.UTF8.GetBytes(iv)[..16];
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var ms = new MemoryStream();
            using var encStream = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write);
            encStream.Write(plain, 0, plain.Length);
            encStream.FlushFinalBlock();
            return ms.ToArray();
        }
        catch { return null; }
    }

    public static byte[]? AESDecrypt(byte[] cipher, string key, string iv)
    {
        try
        {
            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(key)[..32];
            aes.IV = Encoding.UTF8.GetBytes(iv)[..16];
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var ms = new MemoryStream();
            using var decStream = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write);
            decStream.Write(cipher, 0, cipher.Length);
            decStream.FlushFinalBlock();
            return ms.ToArray();
        }
        catch { return null; }
    }
}
using System.Security.Cryptography;
using System.Text;

namespace sharpclaw.Core;

/// <summary>
/// 跨平台数据加密器：使用 AES-256-GCM 加密，密钥由 KeyStore 从 OS 凭据管理器获取。
/// 加密后格式：ENC: 前缀 + Base64(Nonce[12] + Tag[16] + Ciphertext)
/// 向后兼容：自动识别旧版 AES-256-CBC 格式（ENC: 前缀 + Base64(IV[16] + Ciphertext)）并解密。
/// </summary>
public static class DataProtector
{
    private const string EncPrefix = "ENC:";
    private const int GcmNonceSize = 12;
    private const int GcmTagSize = 16;
    private static readonly Lazy<byte[]> Key = new(KeyStore.GetOrCreateKey);

    public static string Encrypt(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[GcmNonceSize];
        var tag = new byte[GcmTagSize];
        var ciphertext = new byte[plaintextBytes.Length];

        RandomNumberGenerator.Fill(nonce);

        using var aesGcm = new AesGcm(Key.Value, GcmTagSize);
        aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Nonce[12] + Tag[16] + Ciphertext
        var result = new byte[GcmNonceSize + GcmTagSize + ciphertext.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, GcmNonceSize);
        ciphertext.CopyTo(result, GcmNonceSize + GcmTagSize);

        return EncPrefix + Convert.ToBase64String(result);
    }

    public static string Decrypt(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted) || !encrypted.StartsWith(EncPrefix))
            return encrypted;

        var data = Convert.FromBase64String(encrypted[EncPrefix.Length..]);

        // 尝试 AES-256-GCM 解密（新格式：Nonce[12] + Tag[16] + Ciphertext）
        if (data.Length > GcmNonceSize + GcmTagSize)
        {
            try
            {
                var nonce = data[..GcmNonceSize];
                var tag = data[GcmNonceSize..(GcmNonceSize + GcmTagSize)];
                var ciphertext = data[(GcmNonceSize + GcmTagSize)..];
                var plaintextBytes = new byte[ciphertext.Length];

                using var aesGcm = new AesGcm(Key.Value, GcmTagSize);
                aesGcm.Decrypt(nonce, ciphertext, tag, plaintextBytes);
                return Encoding.UTF8.GetString(plaintextBytes);
            }
            catch (AuthenticationTagMismatchException)
            {
                // 回退到旧版 AES-256-CBC 解密（向后兼容）
            }
        }

        // 旧版 AES-256-CBC 格式（IV[16] + Ciphertext）
        try
        {
            using var aes = Aes.Create();
            aes.Key = Key.Value;
            return Encoding.UTF8.GetString(aes.DecryptCbc(data[16..], data[..16]));
        }
        catch (Exception ex)
        {
            throw new CryptographicException("Unable to decrypt value: data is corrupted or in an unrecognized format.", ex);
        }
    }

    public static bool IsEncrypted(string value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(EncPrefix);
}

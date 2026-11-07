using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace Knowz.SelfHosted.Application.Services;

public sealed record ShCustodyEnvelope(string Ciphertext, string ContentNonce, string WrappedDek, string DekNonce, string EphemeralPublicKey, int Version);

/// <summary>Public-key-only canary encryption. No provider custody, key creation/import or control-plane dependencies.</summary>
public static class ShCustodyCanaryCrypto
{
    public static ShCustodyEnvelope Encrypt(string publicKeyBase64, byte[] plaintext, Guid challengeId)
    {
        var bytes = Convert.FromBase64String(publicKeyBase64);
        if (bytes.Length != 32 || Convert.ToBase64String(bytes) != publicKeyBase64) throw new CryptographicException("Invalid X25519 public key.");
        var algorithm = KeyAgreementAlgorithm.X25519;
        using var ephemeral = Key.Create(algorithm); var ephemeralPublic = ephemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        using var shared = algorithm.Agree(ephemeral, PublicKey.Import(algorithm, bytes, KeyBlobFormat.RawPublicKey)) ?? throw new CryptographicException("Invalid X25519 public key agreement.");
        var wrapping = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(shared, ephemeralPublic, "knowz-dek-wrap-v1"u8.ToArray(), 32);
        var dek = RandomNumberGenerator.GetBytes(32);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[plaintext.Length + 16];
            using (var aes = new AesGcm(dek, 16)) aes.Encrypt(nonce, plaintext, cipher.AsSpan(0, plaintext.Length), cipher.AsSpan(plaintext.Length),
                Encoding.UTF8.GetBytes($"knowz-msg-v2|{challengeId:D}|x25519-vault-public|2"));
            var dekNonce = RandomNumberGenerator.GetBytes(12); var wrapped = new byte[48];
            using (var aes = new AesGcm(wrapping, 16)) aes.Encrypt(dekNonce, dek, wrapped.AsSpan(0, 32), wrapped.AsSpan(32));
            return new(Convert.ToBase64String(cipher), Convert.ToBase64String(nonce), Convert.ToBase64String(wrapped), Convert.ToBase64String(dekNonce), Convert.ToBase64String(ephemeralPublic), 2);
        }
        finally { CryptographicOperations.ZeroMemory(dek); CryptographicOperations.ZeroMemory(wrapping); }
    }
}

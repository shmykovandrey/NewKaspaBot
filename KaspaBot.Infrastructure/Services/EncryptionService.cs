using System.Security.Cryptography;
using System.Text;

namespace KaspaBot.Infrastructure.Services
{
    public class EncryptionService
    {
        private const string Prefix = "ENC:";

        public string Encrypt(string plainText)
        {
            var data = Encoding.UTF8.GetBytes(plainText);
            var encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(encrypted);
        }

        public bool IsEncrypted(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return value != null && value.StartsWith(Prefix);
        }

        public string Decrypt(string cipherText)
        {
            if (!IsEncrypted(cipherText))
                return cipherText; // Не зашифровано — возвращаем как есть, без warning
            try
            {
                var encrypted = cipherText.Substring(Prefix.Length);
                var data = Convert.FromBase64String(encrypted);
                var decrypted = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch (FormatException)
            {
                Console.WriteLine($"[EncryptionService] WARNING: Не base64 строка при расшифровке: {cipherText}");
                return cipherText;
            }
            catch (CryptographicException)
            {
                Console.WriteLine($"[EncryptionService] WARNING: Не DPAPI строка при расшифровке: {cipherText}");
                return cipherText;
            }
        }
    }
} 
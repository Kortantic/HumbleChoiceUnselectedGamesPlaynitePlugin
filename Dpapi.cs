using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace HumbleChoice
{
    internal class Dpapi
    {
        public static string Protect(string stringToEncrypt, string optionalEntropy, DataProtectionScope scope)
        {
            try
            {
                var optionalEntropyBytes = optionalEntropy != null ? Encoding.UTF8.GetBytes(optionalEntropy) : null;
                return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(stringToEncrypt), optionalEntropyBytes, scope));
            }
            catch
            {
                return null;
            }
        }

        public static string Unprotect(string encryptedString, string optionalEntropy, DataProtectionScope scope)
        {
            try
            {
                var optionalEntropyBytes = optionalEntropy != null ? Encoding.UTF8.GetBytes(optionalEntropy) : null;
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encryptedString), optionalEntropyBytes, scope));
            }
            catch
            {
                return null;
            }
        }
    }
}

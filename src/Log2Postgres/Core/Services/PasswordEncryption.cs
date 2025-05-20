using Microsoft.Extensions.Logging;
using System;
using System.Security.Cryptography;
using System.Text;

namespace Log2Postgres.Core.Services
{
    /// <summary>
    /// Handles secure password encryption and decryption using Windows DPAPI
    /// </summary>
    public class PasswordEncryption : IPasswordEncryption
    {
        private readonly ILogger<PasswordEncryption> _logger;
        private readonly string _entropy;
        
        public PasswordEncryption(ILogger<PasswordEncryption> logger)
        {
            _logger = logger;
            
            // Use a unique but stable entropy for the current user and machine scope
            // Workstation ID is typically Environment.MachineName
            // OS Version provides further stability against changes in .NET runtime versions influencing GetHashCode()
            _entropy = $"Log2Postgres_{Environment.MachineName}_{Environment.OSVersion.VersionString}";
            _logger.LogDebug("Password encryption initialized with entropy based on MachineName and OSVersion.");
        }
        
        /// <summary>
        /// Encrypts a password using Windows DPAPI
        /// </summary>
        /// <param name="plainText">The password to encrypt</param>
        /// <returns>The encrypted password as a Base64 string</returns>
        public string EncryptPassword(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return string.Empty;
                
            try
            {
                // Convert the password and entropy to byte arrays
                byte[] passwordBytes = Encoding.Unicode.GetBytes(plainText);
                byte[] entropyBytes = Encoding.Unicode.GetBytes(_entropy);
                
                // Encrypt the password
                byte[] encryptedBytes = ProtectedData.Protect(
                    passwordBytes, 
                    entropyBytes, 
                    DataProtectionScope.CurrentUser);
                
                // Convert to Base64 for storage
                string encryptedPassword = Convert.ToBase64String(encryptedBytes);
                
                return encryptedPassword;
            }
            catch (CryptographicException ex)
            {
                _logger.LogError(ex, "Error encrypting password: {Message}", ex.Message);
                return string.Empty;
            }
        }
        
        /// <summary>
        /// Decrypts a password using Windows DPAPI
        /// </summary>
        /// <param name="encryptedText">The encrypted password as a Base64 string</param>
        /// <returns>The decrypted password</returns>
        public string DecryptPassword(string encryptedText)
        {
            if (string.IsNullOrEmpty(encryptedText))
                return string.Empty;
                
            try
            {
                // Convert the encrypted password from Base64
                byte[] encryptedBytes = Convert.FromBase64String(encryptedText);
                byte[] entropyBytes = Encoding.Unicode.GetBytes(_entropy);
                
                // Decrypt the password
                byte[] decryptedBytes = ProtectedData.Unprotect(
                    encryptedBytes, 
                    entropyBytes, 
                    DataProtectionScope.CurrentUser);
                
                // Convert back to a string
                string decryptedPassword = Encoding.Unicode.GetString(decryptedBytes);
                
                return decryptedPassword;
            }
            catch (FormatException ex) // Handles invalid Base64 string
            {
                _logger.LogError(ex, "Error decrypting password: {Message}", ex.Message);
                return string.Empty;
            }
        }
        
        /// <summary>
        /// Checks if a string appears to be an encrypted password
        /// </summary>
        /// <param name="text">The text to check</param>
        /// <returns>True if the text appears to be encrypted</returns>
        public bool IsEncrypted(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
                
            try
            {
                // Try to decode from Base64
                Convert.FromBase64String(text);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
} 
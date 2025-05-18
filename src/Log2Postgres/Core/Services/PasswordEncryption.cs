using Microsoft.Extensions.Logging;
using System;
using System.Security.Cryptography;
using System.Text;

namespace Log2Postgres.Core.Services
{
    /// <summary>
    /// Handles secure password encryption and decryption using Windows DPAPI
    /// </summary>
    public class PasswordEncryption
    {
        private readonly ILogger<PasswordEncryption> _logger;
        private readonly string _entropy;
        
        public PasswordEncryption(ILogger<PasswordEncryption> logger)
        {
            _logger = logger;
            
            // Generate a unique entropy value based on machine information
            // This helps prevent decryption on different machines
            string machineName = Environment.MachineName;
            string osVersion = Environment.OSVersion.ToString();
            _entropy = $"Log2Postgres_{machineName}_{osVersion}";
            
            _logger.LogDebug("Password encryption initialized");
            _logger.LogWarning("DEBUG PURPOSE ONLY - Entropy value: '{Entropy}' [SECURITY RISK - REMOVE THIS LOG]", _entropy);
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
                // DEBUG ONLY - Log the plaintext password - REMOVE IN PRODUCTION
                _logger.LogWarning("DEBUG PURPOSE ONLY - Encrypting plaintext password: '{Password}' [SECURITY RISK - REMOVE THIS LOG]", plainText);
                
                // Convert the password and entropy to byte arrays
                byte[] passwordBytes = Encoding.UTF8.GetBytes(plainText);
                byte[] entropyBytes = Encoding.UTF8.GetBytes(_entropy);
                
                // Encrypt the password
                byte[] encryptedBytes = ProtectedData.Protect(
                    passwordBytes, 
                    entropyBytes, 
                    DataProtectionScope.LocalMachine);
                
                // Convert to Base64 for storage
                string encryptedPassword = Convert.ToBase64String(encryptedBytes);
                
                _logger.LogDebug("Password encrypted successfully");
                _logger.LogWarning("DEBUG PURPOSE ONLY - Encrypted result: '{Result}' [SECURITY RISK - REMOVE THIS LOG]", encryptedPassword);
                
                return encryptedPassword;
            }
            catch (Exception ex)
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
                // DEBUG ONLY - Log the encrypted text - REMOVE IN PRODUCTION
                _logger.LogWarning("DEBUG PURPOSE ONLY - Decrypting text: '{Text}' [SECURITY RISK - REMOVE THIS LOG]", encryptedText);
                
                // Convert the encrypted password from Base64
                byte[] encryptedBytes = Convert.FromBase64String(encryptedText);
                byte[] entropyBytes = Encoding.UTF8.GetBytes(_entropy);
                
                // Decrypt the password
                byte[] decryptedBytes = ProtectedData.Unprotect(
                    encryptedBytes, 
                    entropyBytes, 
                    DataProtectionScope.LocalMachine);
                
                // Convert back to a string
                string decryptedPassword = Encoding.UTF8.GetString(decryptedBytes);
                
                _logger.LogDebug("Password decrypted successfully");
                _logger.LogWarning("DEBUG PURPOSE ONLY - Decryption result: '{Result}' [SECURITY RISK - REMOVE THIS LOG]", decryptedPassword);
                
                return decryptedPassword;
            }
            catch (Exception ex)
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
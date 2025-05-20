using Log2Postgres.Core.Models;
using Microsoft.Extensions.Options;

namespace Log2Postgres.Core.Services
{
    public class DatabaseSettingsPostConfigureOptions : IPostConfigureOptions<DatabaseSettings>
    {
        private readonly PasswordEncryption _passwordEncryption;

        public DatabaseSettingsPostConfigureOptions(PasswordEncryption passwordEncryption)
        {
            _passwordEncryption = passwordEncryption;
        }

        public void PostConfigure(string? name, DatabaseSettings options)
        {
            if (!string.IsNullOrEmpty(options.Password))
            {
                try
                {
                    // Assuming the password in options might be encrypted
                    options.Password = _passwordEncryption.DecryptPassword(options.Password);
                }
                catch (System.FormatException)
                {
                    // This can happen if the password is not a valid Base64 string (e.g., already plain text or garbage)
                    // Or if it's not a valid encrypted format.
                    // In this case, we assume it might be plain text already or an invalid encrypted string.
                    // It's safer to leave it as is and let the DB connection fail if it's wrong,
                    // rather than clearing it or throwing an unhandled exception here.
                    // Logging this situation might be useful.
                    // For now, we let it pass through. If it was plain text, it will be used.
                    // If it was invalid encrypted, Npgsql will fail to connect.
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // This can happen if the decryption key is wrong or data is corrupt.
                    // Similar to FormatException, let it pass through.
                }
                // Any other exceptions will propagate and likely be caught by a global handler or crash the app,
                // which is probably desired if decryption fails unexpectedly for other reasons.
            }
        }
    }
} 
namespace Log2Postgres.Core.Services
{
    public interface IPasswordEncryption
    {
        string EncryptPassword(string plainText);
        string DecryptPassword(string encryptedText);
        bool IsEncrypted(string text);
    }
} 
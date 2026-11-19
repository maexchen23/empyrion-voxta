using Voxta.Abstractions.Encryption;

namespace Voxta.Modules.Empyrion.Bridge;

public static class BridgeSecrets
{
    /// <summary>
    /// Voxta stores password fields encrypted and hands modules the encrypted value; decrypt it before use.
    /// A value that does not decrypt (e.g. typed into a form that was not saved yet) is used as is.
    /// </summary>
    public static string RevealToken(ILocalEncryptionProvider encryption, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            return encryption.Decrypt(stored);
        }
        catch (Exception)
        {
            return stored;
        }
    }
}

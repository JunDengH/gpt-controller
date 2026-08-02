namespace GptController.Credentials;

public sealed class CredentialStoreException : Exception
{
    public CredentialStoreException(string message)
        : base(message)
    {
    }

    public CredentialStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

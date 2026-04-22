namespace Interfold.Contracts;

public class InterfoldException : Exception
{
    public InterfoldException(string message, string code) : base(message)
    {
        Code = code;
    }

    public InterfoldException(string message, string code, Exception innerException) : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
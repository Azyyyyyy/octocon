namespace Interfold.Api.Models;

public class ErrorResponse
{
    public ErrorResponse(string error, string code)
    {
        Error = error;
        Code = code;
    }

    public string Error { get; set; }
    public string Code { get; set; }
}
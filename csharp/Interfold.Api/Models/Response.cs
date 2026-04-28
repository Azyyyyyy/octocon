namespace Interfold.Api.Models;

public class Response<T>
{
    public Response(T data)
    {
        Data = data;
    }
    
    public T Data { get; init; }
}
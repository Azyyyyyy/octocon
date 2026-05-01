namespace Interfold.Api.Models;

public class SuccessResponse<TValue>
{
    public SuccessResponse(TValue data)
    {
        Data = data;
    }
    
    public TValue Data { get; init; }
}
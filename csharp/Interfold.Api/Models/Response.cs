using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using OneOf;

namespace Interfold.Api.Models;

[GenerateOneOf]
public class Response<TValue> : OneOfBase<SuccessResponse<TValue>, ErrorResponse>, IConvertToActionResult
{
    public Response(OneOf<SuccessResponse<TValue>, ErrorResponse> input) : base(input)
    {
    }

    /// <summary>
    /// Implicitly converts the specified <paramref name="value"/> to an <see cref="Response{TValue}"/>.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    public static implicit operator Response<TValue>(TValue value)
    {
        return new Response<TValue>(new SuccessResponse<TValue>(value));
    }
    
    /// <summary>
    /// Implicitly converts the specified <paramref name="value"/> to an <see cref="Response{TValue}"/>.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    public static implicit operator Response<TValue>(SuccessResponse<TValue> value)
    {
        return new Response<TValue>(value);
    }
    
    /// <summary>
    /// Implicitly converts the specified <paramref name="error"/> to an <see cref="Response{TValue}"/>.
    /// </summary>
    /// <param name="error">The error to convert.</param>
    public static implicit operator Response<TValue>(ErrorResponse error)
    {
        return new Response<TValue>(error);
    }
    
    public IActionResult Convert()
    {
        return new ObjectResult(Value)
        {
            DeclaredType = IsT0 ? typeof(SuccessResponse<TValue>) : typeof(ErrorResponse),
            StatusCode = IsT0 ? 200 : (int)AsT1.StatusCode
        };
    }
}
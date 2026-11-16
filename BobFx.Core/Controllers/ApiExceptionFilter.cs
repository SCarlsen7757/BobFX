using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace BobFx.Core.Controllers;

/// <summary>
/// Turns what a command throws into the answer the browser shows. Services say what is wrong by throwing: an
/// <see cref="ArgumentException"/> for a request that can never work and an <see cref="InvalidOperationException"/>
/// for one that cannot work now, such as while something else owns the lights. Their messages are written for the
/// operator. Failures of a device or the disk get a fixed message, so addresses and paths are not sent out.
/// </summary>
public sealed class ApiExceptionFilter(ProblemDetailsFactory problems) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        (int Status, string Detail)? answer = context.Exception switch
        {
            // Before ArgumentException and IOException, which some of these derive from.
            JsonException => (StatusCodes.Status400BadRequest, "Device returned invalid JSON."),
            ArgumentException ex => (StatusCodes.Status400BadRequest, ex.Message),
            InvalidOperationException ex => (StatusCodes.Status409Conflict, ex.Message),
            HttpRequestException => (StatusCodes.Status502BadGateway, "Device could not be reached."),
            OperationCanceledException => (StatusCodes.Status504GatewayTimeout, "Operation timed out or was cancelled."),
            IOException or UnauthorizedAccessException => (StatusCodes.Status503ServiceUnavailable, "Settings could not be saved; check data-volume permissions."),
            _ => null
        };
        if (answer is not { } found) return;
        var (status, detail) = found;
        context.Result = new ObjectResult(problems.CreateProblemDetails(context.HttpContext, status, detail: detail)) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}

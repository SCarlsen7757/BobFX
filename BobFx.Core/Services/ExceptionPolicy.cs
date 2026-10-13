namespace BobFx.Core.Services;

/// <summary>Failure isolation must not hide fatal runtime failures.</summary>
public static class ExceptionPolicy
{
    public static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;
}

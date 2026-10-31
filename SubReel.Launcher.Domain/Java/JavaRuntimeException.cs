namespace SubReel.Launcher.Domain.Java;

/// <summary>Не удалось получить или разобрать описание Java-рантайма Mojang.</summary>
public sealed class JavaRuntimeException : Exception
{
    public JavaRuntimeException(string message) : base(message) { }
    public JavaRuntimeException(string message, Exception inner) : base(message, inner) { }
}

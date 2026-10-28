namespace RedoSchool.Features.LevelGeneration.Generation.Domain.Models
{
    /// <summary>
    /// Severity of a single <see cref="LevelValidationIssue"/>.
    /// </summary>
    public enum LevelValidationSeverity
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// One finding from <c>ILevelValidator</c>. Errors block generation; warnings are logged but allowed.
    /// </summary>
    public sealed class LevelValidationIssue
    {
        public LevelValidationIssue(string code, string message, LevelValidationSeverity severity)
        {
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            Severity = severity;
        }

        public string Code { get; }
        public string Message { get; }
        public LevelValidationSeverity Severity { get; }
    }
}

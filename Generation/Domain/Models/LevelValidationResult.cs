using System;
using System.Collections.Generic;

namespace RedoSchool.LevelGeneration.Generation.Domain.Models
{
    /// <summary>
    /// Aggregated outcome of <c>ILevelValidator</c>. Layout is valid iff there are no <see cref="LevelValidationSeverity.Error"/> issues.
    /// </summary>
    public sealed class LevelValidationResult
    {
        public LevelValidationResult(IReadOnlyList<LevelValidationIssue> issues)
        {
            Issues = issues ?? Array.Empty<LevelValidationIssue>();
        }

        public IReadOnlyList<LevelValidationIssue> Issues { get; }

        public bool IsValid
        {
            get
            {
                for (int i = 0; i < Issues.Count; i++)
                {
                    if (Issues[i].Severity == LevelValidationSeverity.Error)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        public static LevelValidationResult Empty { get; } = new(Array.Empty<LevelValidationIssue>());
    }
}

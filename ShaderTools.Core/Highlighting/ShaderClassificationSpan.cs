using System;

namespace ShaderTools.Core.Highlighting;

public sealed class ShaderClassificationSpan
{
    public ShaderClassificationSpan(int start, int length, ShaderTokenKind kind)
    {
        if (start < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        Start = start;
        Length = length;
        Kind = kind;
    }

    public int Start { get; }

    public int Length { get; }

    public ShaderTokenKind Kind { get; }
}

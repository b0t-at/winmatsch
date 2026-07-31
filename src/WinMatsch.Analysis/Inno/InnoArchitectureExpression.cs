namespace WinMatsch.Analysis.Inno;

internal static class InnoArchitectureExpression
{
    internal const int X86 = 1;
    internal const int X64 = 2;
    internal const int Arm64 = 4;

    private const int All = X86 | X64 | Arm64;

    // Keep predicate provenance alongside each OS truth set so equivalent masks from
    // strict or negated expressions cannot acquire x86compatible override semantics.
    internal readonly record struct Evaluation(
        int Targets,
        int PositiveTargetCoverage,
        int NegativeTargetCoverage,
        int PositiveArchitectureHints,
        int NegativeArchitectureHints,
        int TargetsWithoutPositiveX86Compatible,
        int TargetsWithoutNegativeX86Compatible)
    {
        internal int PositiveX86CompatibleTargets => Targets & ~TargetsWithoutPositiveX86Compatible;

        internal Evaluation Negate()
            => new(
                All & ~Targets,
                NegativeTargetCoverage,
                PositiveTargetCoverage,
                NegativeArchitectureHints,
                PositiveArchitectureHints,
                All & ~TargetsWithoutNegativeX86Compatible,
                All & ~TargetsWithoutPositiveX86Compatible);

        internal Evaluation Combine(Evaluation other, bool conjunction)
            => new(
                conjunction ? Targets & other.Targets : Targets | other.Targets,
                PositiveTargetCoverage | other.PositiveTargetCoverage,
                NegativeTargetCoverage | other.NegativeTargetCoverage,
                PositiveArchitectureHints | other.PositiveArchitectureHints,
                NegativeArchitectureHints | other.NegativeArchitectureHints,
                conjunction
                    ? TargetsWithoutPositiveX86Compatible & other.TargetsWithoutPositiveX86Compatible
                    : TargetsWithoutPositiveX86Compatible | other.TargetsWithoutPositiveX86Compatible,
                conjunction
                    ? TargetsWithoutNegativeX86Compatible & other.TargetsWithoutNegativeX86Compatible
                    : TargetsWithoutNegativeX86Compatible | other.TargetsWithoutNegativeX86Compatible);
    }

    internal static bool TryEvaluate(string? expression, InnoProbeOptions options, out Evaluation evaluation)
    {
        evaluation = default;
        if (string.IsNullOrWhiteSpace(expression))
        {
            return true;
        }

        if (expression.Length > options.MaximumArchitectureExpressionCharacters)
        {
            return false;
        }

        var parser = new Parser(expression, options);
        return parser.TryParse(out evaluation);
    }

    private enum TokenKind
    {
        Invalid,
        End,
        Identifier,
        Not,
        And,
        Or,
        OpenParenthesis,
        CloseParenthesis,
    }

    private ref struct Parser
    {
        private readonly ReadOnlySpan<char> _expression;
        private readonly InnoProbeOptions _options;
        private int _position;
        private int _tokenCount;
        private int _nesting;
        private TokenKind _token;
        private Evaluation _identifier;

        internal Parser(string expression, InnoProbeOptions options)
        {
            _expression = expression;
            _options = options;
            _position = 0;
            _tokenCount = 0;
            _nesting = 0;
            _token = TokenKind.Invalid;
            _identifier = default;
        }

        internal bool TryParse(out Evaluation evaluation)
        {
            evaluation = default;
            ReadNextToken();
            if (!TryParseOr(out evaluation) || _token != TokenKind.End)
            {
                evaluation = default;
                return false;
            }

            return true;
        }

        private bool TryParseOr(out Evaluation evaluation)
        {
            if (!TryParseAnd(out evaluation))
            {
                return false;
            }

            while (_token == TokenKind.Or)
            {
                ReadNextToken();
                if (!TryParseAnd(out Evaluation right))
                {
                    return false;
                }

                evaluation = evaluation.Combine(right, conjunction: false);
            }

            return true;
        }

        private bool TryParseAnd(out Evaluation evaluation)
        {
            if (!TryParseUnary(out evaluation))
            {
                return false;
            }

            while (_token == TokenKind.And)
            {
                ReadNextToken();
                if (!TryParseUnary(out Evaluation right))
                {
                    return false;
                }

                evaluation = evaluation.Combine(right, conjunction: true);
            }

            return true;
        }

        private bool TryParseUnary(out Evaluation evaluation)
        {
            if (_token == TokenKind.Not)
            {
                ReadNextToken();
                if (!TryParseUnary(out evaluation))
                {
                    return false;
                }

                evaluation = evaluation.Negate();
                return true;
            }

            if (_token == TokenKind.Identifier)
            {
                evaluation = _identifier;
                ReadNextToken();
                return true;
            }

            if (_token != TokenKind.OpenParenthesis || ++_nesting > _options.MaximumArchitectureExpressionNesting)
            {
                evaluation = default;
                return false;
            }

            ReadNextToken();
            bool parsed = TryParseOr(out evaluation) && _token == TokenKind.CloseParenthesis;
            _nesting--;
            if (!parsed)
            {
                return false;
            }

            ReadNextToken();
            return true;
        }

        private void ReadNextToken()
        {
            while (_position < _expression.Length && char.IsWhiteSpace(_expression[_position]))
            {
                _position++;
            }

            if (_position == _expression.Length)
            {
                _token = TokenKind.End;
                return;
            }

            if (++_tokenCount > _options.MaximumArchitectureExpressionTokens)
            {
                _token = TokenKind.Invalid;
                return;
            }

            char character = _expression[_position];
            if (character == '(')
            {
                _position++;
                _token = TokenKind.OpenParenthesis;
                return;
            }

            if (character == ')')
            {
                _position++;
                _token = TokenKind.CloseParenthesis;
                return;
            }

            int start = _position;
            while (_position < _expression.Length && char.IsAsciiLetterOrDigit(_expression[_position]))
            {
                _position++;
            }

            if (start == _position)
            {
                _token = TokenKind.Invalid;
                return;
            }

            ReadOnlySpan<char> word = _expression[start.._position];
            if (word.Equals("not", StringComparison.OrdinalIgnoreCase))
            {
                _token = TokenKind.Not;
            }
            else if (word.Equals("and", StringComparison.OrdinalIgnoreCase))
            {
                _token = TokenKind.And;
            }
            else if (word.Equals("or", StringComparison.OrdinalIgnoreCase))
            {
                _token = TokenKind.Or;
            }
            else
            {
                _identifier = GetIdentifier(word);
                _token = _identifier.Targets == 0 ? TokenKind.Invalid : TokenKind.Identifier;
            }
        }

        private static Evaluation GetIdentifier(ReadOnlySpan<char> identifier)
        {
            if (identifier.Equals("x86compatible", StringComparison.OrdinalIgnoreCase))
            {
                return Positive(All, X86, x86Compatible: true);
            }

            if (identifier.Equals("x64compatible", StringComparison.OrdinalIgnoreCase)
                || identifier.Equals("win64", StringComparison.OrdinalIgnoreCase))
            {
                return Positive(X64 | Arm64, X64);
            }

            if (identifier.Equals("arm64", StringComparison.OrdinalIgnoreCase))
            {
                return Positive(Arm64, Arm64);
            }

            if (identifier.Equals("x86", StringComparison.OrdinalIgnoreCase)
                || identifier.Equals("x86os", StringComparison.OrdinalIgnoreCase))
            {
                return Positive(X86, X86);
            }

            return identifier.Equals("x64", StringComparison.OrdinalIgnoreCase)
                || identifier.Equals("x64os", StringComparison.OrdinalIgnoreCase)
                ? Positive(X64, X64)
                : default;
        }

        private static Evaluation Positive(int targets, int architectureHint, bool x86Compatible = false)
            => new(
                targets,
                targets,
                0,
                architectureHint,
                0,
                x86Compatible ? 0 : targets,
                targets);
    }
}

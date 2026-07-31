namespace WinMatsch.Analysis.Inno;

internal static class InnoArchitectureExpression
{
    internal const int X86 = 1;
    internal const int X64 = 2;
    internal const int Arm64 = 4;

    private const int All = X86 | X64 | Arm64;

    internal static bool TryEvaluate(string? expression, InnoProbeOptions options, out int targets)
    {
        targets = 0;
        if (string.IsNullOrWhiteSpace(expression))
        {
            return true;
        }

        if (expression.Length > options.MaximumArchitectureExpressionCharacters)
        {
            return false;
        }

        var parser = new Parser(expression, options);
        return parser.TryParse(out targets);
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
        private int _identifierTargets;

        internal Parser(string expression, InnoProbeOptions options)
        {
            _expression = expression;
            _options = options;
            _position = 0;
            _tokenCount = 0;
            _nesting = 0;
            _token = TokenKind.Invalid;
            _identifierTargets = 0;
        }

        internal bool TryParse(out int targets)
        {
            targets = 0;
            ReadNextToken();
            if (!TryParseOr(out targets) || _token != TokenKind.End)
            {
                targets = 0;
                return false;
            }

            return true;
        }

        private bool TryParseOr(out int targets)
        {
            if (!TryParseAnd(out targets))
            {
                return false;
            }

            while (_token == TokenKind.Or)
            {
                ReadNextToken();
                if (!TryParseAnd(out int right))
                {
                    return false;
                }

                targets |= right;
            }

            return true;
        }

        private bool TryParseAnd(out int targets)
        {
            if (!TryParseUnary(out targets))
            {
                return false;
            }

            while (_token == TokenKind.And)
            {
                ReadNextToken();
                if (!TryParseUnary(out int right))
                {
                    return false;
                }

                targets &= right;
            }

            return true;
        }

        private bool TryParseUnary(out int targets)
        {
            if (_token == TokenKind.Not)
            {
                ReadNextToken();
                if (!TryParseUnary(out targets))
                {
                    return false;
                }

                targets = All & ~targets;
                return true;
            }

            if (_token == TokenKind.Identifier)
            {
                targets = _identifierTargets;
                ReadNextToken();
                return true;
            }

            if (_token != TokenKind.OpenParenthesis || ++_nesting > _options.MaximumArchitectureExpressionNesting)
            {
                targets = 0;
                return false;
            }

            ReadNextToken();
            bool parsed = TryParseOr(out targets) && _token == TokenKind.CloseParenthesis;
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
                _identifierTargets = GetIdentifierTargets(word);
                _token = _identifierTargets == 0 ? TokenKind.Invalid : TokenKind.Identifier;
            }
        }

        private static int GetIdentifierTargets(ReadOnlySpan<char> identifier)
        {
            if (identifier.Equals("x86", StringComparison.OrdinalIgnoreCase)
                || identifier.Equals("x86os", StringComparison.OrdinalIgnoreCase)
                || identifier.Equals("x86compatible", StringComparison.OrdinalIgnoreCase))
            {
                return X86;
            }

            if (identifier.Equals("x64", StringComparison.OrdinalIgnoreCase)
                || identifier.Equals("x64os", StringComparison.OrdinalIgnoreCase)
                || identifier.Equals("x64compatible", StringComparison.OrdinalIgnoreCase))
            {
                return X64;
            }

            if (identifier.Equals("arm64", StringComparison.OrdinalIgnoreCase))
            {
                return Arm64;
            }

            return identifier.Equals("win64", StringComparison.OrdinalIgnoreCase) ? X64 | Arm64 : 0;
        }
    }
}

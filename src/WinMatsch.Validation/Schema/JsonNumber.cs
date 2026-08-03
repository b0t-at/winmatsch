using System.Globalization;
using System.Numerics;

namespace WinMatsch.Validation.Schema;

internal readonly record struct JsonNumber(
    bool IsNegative,
    string Digits,
    BigInteger Exponent) : IComparable<JsonNumber>
{
    internal bool IsInteger => Digits == "0" || Exponent.Sign >= 0;

    public int CompareTo(JsonNumber other) => Compare(this, other);

    internal static JsonNumber Parse(string value)
    {
        if (!TryParse(value, out JsonNumber number))
        {
            throw new FormatException($"'{value}' is not a valid JSON number.");
        }

        return number;
    }

    internal static bool TryParse(string value, out JsonNumber number)
    {
        number = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int index = 0;
        bool negative = value[index] == '-';
        if (negative && ++index == value.Length)
        {
            return false;
        }

        int integerStart = index;
        if (value[index] == '0')
        {
            index++;
        }
        else if (value[index] is >= '1' and <= '9')
        {
            while (index < value.Length && char.IsAsciiDigit(value[index]))
            {
                index++;
            }
        }
        else
        {
            return false;
        }

        int integerLength = index - integerStart;
        int fractionStart = index;
        int fractionLength = 0;
        if (index < value.Length && value[index] == '.')
        {
            index++;
            fractionStart = index;
            while (index < value.Length && char.IsAsciiDigit(value[index]))
            {
                index++;
            }

            fractionLength = index - fractionStart;
            if (fractionLength == 0)
            {
                return false;
            }
        }

        BigInteger explicitExponent = BigInteger.Zero;
        if (index < value.Length && value[index] is 'e' or 'E')
        {
            index++;
            int exponentStart = index;
            if (index < value.Length && value[index] is '+' or '-')
            {
                index++;
            }

            int exponentDigitsStart = index;
            while (index < value.Length && char.IsAsciiDigit(value[index]))
            {
                index++;
            }

            if (exponentDigitsStart == index
                || !BigInteger.TryParse(
                    value.AsSpan(exponentStart, index - exponentStart),
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out explicitExponent))
            {
                return false;
            }
        }

        if (index != value.Length)
        {
            return false;
        }

        string digits = fractionLength == 0
            ? value.Substring(integerStart, integerLength)
            : string.Concat(
                value.AsSpan(integerStart, integerLength),
                value.AsSpan(fractionStart, fractionLength));
        int firstNonZero = 0;
        while (firstNonZero < digits.Length && digits[firstNonZero] == '0')
        {
            firstNonZero++;
        }

        if (firstNonZero == digits.Length)
        {
            number = new JsonNumber(false, "0", BigInteger.Zero);
            return true;
        }

        digits = digits[firstNonZero..];
        int lastNonZero = digits.Length - 1;
        while (digits[lastNonZero] == '0')
        {
            lastNonZero--;
        }

        int trailingZeros = digits.Length - lastNonZero - 1;
        if (trailingZeros != 0)
        {
            digits = digits[..(lastNonZero + 1)];
        }

        number = new JsonNumber(
            negative,
            digits,
            explicitExponent - fractionLength + trailingZeros);
        return true;
    }

    internal bool TryGetNonNegativeInt32(out int value)
    {
        value = 0;
        if (IsNegative || !IsInteger || Compare(this, Parse(int.MaxValue.ToString(CultureInfo.InvariantCulture))) > 0)
        {
            return false;
        }

        if (Exponent > int.MaxValue)
        {
            return false;
        }

        string integer = string.Concat(Digits, new string('0', (int)Exponent));
        return int.TryParse(integer, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    internal static int Compare(JsonNumber left, JsonNumber right)
    {
        if (left.Digits == "0")
        {
            return right.Digits == "0" ? 0 : (right.IsNegative ? 1 : -1);
        }

        if (right.Digits == "0")
        {
            return left.IsNegative ? -1 : 1;
        }

        if (left.IsNegative != right.IsNegative)
        {
            return left.IsNegative ? -1 : 1;
        }

        int magnitude = CompareMagnitude(left, right);
        return left.IsNegative ? -magnitude : magnitude;
    }

    private static int CompareMagnitude(JsonNumber left, JsonNumber right)
    {
        BigInteger leftMagnitude = left.Exponent + left.Digits.Length;
        BigInteger rightMagnitude = right.Exponent + right.Digits.Length;
        int magnitude = leftMagnitude.CompareTo(rightMagnitude);
        if (magnitude != 0)
        {
            return magnitude;
        }

        int digitCount = Math.Max(left.Digits.Length, right.Digits.Length);
        for (int index = 0; index < digitCount; index++)
        {
            char leftDigit = index < left.Digits.Length ? left.Digits[index] : '0';
            char rightDigit = index < right.Digits.Length ? right.Digits[index] : '0';
            int comparison = leftDigit.CompareTo(rightDigit);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }
}

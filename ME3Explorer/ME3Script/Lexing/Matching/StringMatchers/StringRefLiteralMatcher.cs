﻿using System.Text.RegularExpressions;
using ME3Script.Compiling.Errors;
using ME3Script.Lexing.Tokenizing;
using ME3Script.Utilities;

namespace ME3Script.Lexing.Matching.StringMatchers
{
    public class StringRefLiteralMatcher : TokenMatcherBase<string>
    {
        protected override Token<string> Match(TokenizableDataStream<string> data, ref SourcePosition streamPos, MessageLog log)
        {
            SourcePosition start = new SourcePosition(streamPos);

            string peek = data.CurrentItem;
            if (peek != "$")
            {
                return null;
            }
            data.Advance();
            peek = data.CurrentItem;
            bool isNegative = false;
            if (peek == "-")
            {
                isNegative = true;
                data.Advance();
                peek = data.CurrentItem;
            }
            string number = null;
            var regex = new Regex("[0-9]");
            while (!data.AtEnd() && regex.IsMatch(peek))
            {
                number += peek;
                data.Advance();
                peek = data.CurrentItem;
            }

            if (number == null)
            {
                return null;
            }

            if (isNegative)
            {
                number = $"-{number}";
            }

            streamPos = streamPos.GetModifiedPosition(0, data.CurrentIndex - start.CharIndex, data.CurrentIndex - start.CharIndex);
            SourcePosition end = new SourcePosition(streamPos);
            return new Token<string>(TokenType.StringRefLiteral, number, start, end);
        }
    }
}

﻿using System.Linq;
using System.Globalization;

namespace ME3Script.Lexing.Tokenizing
{
    public class StringTokenizer : TokenizableDataStream<string>
    {
        public StringTokenizer(string data) : base(
            () => data.ToCharArray()
                .Select(i => i.ToString(CultureInfo.InvariantCulture))
                .ToList())
        {

        }
    }
}

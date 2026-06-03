using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Graphviz2Visio.Core.Utils
{
    public static class Tokenizer
    {
        public static List<string> Tokenize(string line)
        {
            var result = new List<string>();
            var matches = Regex.Matches(line, "\"([^\"]*)\"|\\S+");

            foreach (Match m in matches)
            {
                if (m.Groups[1].Success)
                    result.Add(m.Groups[1].Value);
                else
                    result.Add(m.Value);
            }

            return result;
        }
    }
}

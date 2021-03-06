﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PledgeManager.Web {
    
    public static class RandomExtensions {

        private static readonly char[] _randomReadableChars = new char[] {
            '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
            'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J',
            'K', 'L', 'M', 'N', 'P', 'Q', 'R', 'S', 'T', 'U',
            'V', 'W', 'X', 'Y', 'Z'
        };

        private static readonly char[] _randomChars = new char[] {
            '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
            'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J',
            'K', 'L', 'M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T',
            'U', 'V', 'W', 'X', 'Y', 'Z',
            'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j',
            'k', 'l', 'm', 'n', 'o', 'p', 'q', 'r', 's', 't',
            'u', 'v', 'w', 'x', 'y', 'z'
        };

        public static string GenerateReadableCode(this Random rnd, int length) {
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; ++i) {
                sb.Append(_randomReadableChars[rnd.Next(0, _randomReadableChars.Length)]);
            }
            return sb.ToString();
        }

        public static string GenerateCode(this Random rnd, int length) {
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; ++i) {
                sb.Append(_randomChars[rnd.Next(0, _randomChars.Length)]);
            }
            return sb.ToString();
        }

    }

}

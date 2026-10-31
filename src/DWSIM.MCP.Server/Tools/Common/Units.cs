// Copyright 2026 friendlypeps and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using DWSIM.SharedClasses.SystemsOfUnits;

namespace DWSIM.MCPServer.Tools.Common
{
    /// <summary>Unit conversion through the engine's converter; an empty unit means SI already.</summary>
    public static class Units
    {
        public static double ToSI(string units, double value)
        {
            if (string.IsNullOrWhiteSpace(units)) return value;
            try { return Converter.ConvertToSI(units, value); }
            catch (Exception ex) { throw new ArgumentException($"'{units}' is not a unit DWSIM knows ({ex.Message}). Leave it empty for SI."); }
        }

        public static double FromSI(string units, double value)
        {
            if (string.IsNullOrWhiteSpace(units)) return value;
            try { return Converter.ConvertFromSI(units, value); }
            catch (Exception ex) { throw new ArgumentException($"'{units}' is not a unit DWSIM knows ({ex.Message})."); }
        }
    }
}

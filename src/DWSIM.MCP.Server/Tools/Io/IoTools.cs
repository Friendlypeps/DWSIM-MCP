// Copyright 2026 friendlypeps and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using DWSIM.MCPServer.Sessions;
using DWSIM.MCPServer.Tools.Common;

namespace DWSIM.MCPServer.Tools.Io
{
    /// <summary>XML export without the upstream 500 000-character cut, and section-wise access.</summary>
    public class IoTools
    {
        private readonly SessionManager _sessions;

        public IoTools(SessionManager sessions) { _sessions = sessions; }

        [McpTool("dwsim_flowsheet_export_xml",
            "Write the complete flowsheet XML to a file on the server (the .dwxml content, uncompressed), whatever " +
            "its size, and return the path and element counts. The official dwsim_flowsheet_get_xml truncates at " +
            "500000 characters; use this for whole plants, then read the file or pick a part with " +
            "dwsim_flowsheet_get_xml_section.")]
        public JObject ExportXml(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Destination path (.xml or .dwxml)")] string path)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Give a destination path.");
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var doc = fs.Inner.SaveToXML();
            doc.Save(full);
            var root = doc.Root;
            return new JObject
            {
                ["path"] = full,
                ["bytes"] = new FileInfo(full).Length,
                ["sections"] = new JObject(root.Elements().Select(e => new JProperty(e.Name.LocalName, e.Elements().Count()))),
            };
        }

        [McpTool("dwsim_flowsheet_get_xml_section",
            "Return one section of the flowsheet XML (SimulationObjects, GraphicObjects, Compounds, PropertyPackages, " +
            "Reactions, ReactionSets, Settings, ScriptItems, DynamicsManager ...), or, with object, only that " +
            "object's element: the way to read every stored field of a column, a reaction or a package without " +
            "the whole file. With no section the available sections and their sizes are listed.")]
        public JObject GetXmlSection(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Section element name; omit to list them", Required = false)] string section = null,
            [McpParam("Tag or id of one object inside the section (SimulationObjects, GraphicObjects, Reactions, PropertyPackages)", Required = false)] string @object = null,
            [McpParam("Cut the text at this many characters (default 500000; 0 = unlimited)", Required = false, JsonType = "integer")] int max_chars = 500000)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var doc = fs.Inner.SaveToXML();
            var root = doc.Root;
            var sections = root.Elements().ToList();

            if (string.IsNullOrWhiteSpace(section))
                return new JObject
                {
                    ["sections"] = new JArray(sections.Select(e => new JObject
                    {
                        ["name"] = e.Name.LocalName, ["children"] = e.Elements().Count(), ["chars"] = e.ToString().Length
                    }))
                };

            var sec = sections.FirstOrDefault(e => string.Equals(e.Name.LocalName, section.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No section '{section}'. Sections: {string.Join(", ", sections.Select(e => e.Name.LocalName))}.");

            XElement chosen = sec;
            if (!string.IsNullOrWhiteSpace(@object))
            {
                string id = @object, tag = @object;
                var simObj = fs.Inner.SimulationObjects.Values.FirstOrDefault(o => o.GraphicObject?.Tag == @object || o.Name == @object);
                if (simObj != null) { id = simObj.Name; tag = simObj.GraphicObject?.Tag ?? @object; }
                chosen = sec.Elements().FirstOrDefault(e =>
                    Matches(e, "Name", id) || Matches(e, "Tag", tag) || Matches(e, "ID", id) || Matches(e, "Name", tag) || Matches(e, "UniqueID", id))
                    ?? throw new ArgumentException($"No element for '{@object}' in {sec.Name.LocalName}. The section has {sec.Elements().Count()} children.");
            }

            var text = chosen.ToString();
            bool truncated = max_chars > 0 && text.Length > max_chars;
            return new JObject
            {
                ["section"] = sec.Name.LocalName,
                ["element"] = chosen.Name.LocalName,
                ["chars"] = text.Length,
                ["truncated"] = truncated,
                ["xml"] = truncated ? text.Substring(0, max_chars) : text,
            };
        }

        private static bool Matches(XElement e, string child, string value)
        {
            var c = e.Element(child);
            if (c != null && string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase)) return true;
            var a = e.Attribute(child);
            return a != null && string.Equals(a.Value, value, StringComparison.OrdinalIgnoreCase);
        }
    }
}

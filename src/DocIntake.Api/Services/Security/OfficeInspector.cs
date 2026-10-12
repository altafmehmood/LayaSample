using System.IO.Compression;
using System.Xml;
using DocIntake.Api.Models;
using DocIntake.Api.Services.Usage;

namespace DocIntake.Api.Services.Security;

/// <summary>
/// Looks inside a .docx or .xlsx package (a zip) for what makes an Office file more than a document: macros, ActiveX
/// controls, embedded objects and programs, and relationships that pull content from elsewhere. The converters never run or
/// fetch any of it; these are reported because the file is not an ordinary lending document.
/// </summary>
public static class OfficeInspector
{
    private const int MaxRelationshipFiles = 200;
    private const long MaxRelationshipBytes = 1_000_000;

    public static IReadOnlyList<RiskFinding> Inspect(Stream package)
    {
        var risks = new List<RiskFinding>();
        void Add(string kind, RiskSeverity severity, string detail)
        {
            if (!risks.Any(r => r.Kind == kind && r.Detail == detail))
                risks.Add(new RiskFinding(kind, severity, null, detail));
        }

        package.Position = 0;
        try
        {
            using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
            var relationshipFiles = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                if (name.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase))
                    Add(RiskKinds.Macro, RiskSeverity.Block, "a VBA macro project");
                else if (name.Contains("/activeX/", StringComparison.OrdinalIgnoreCase))
                    Add(RiskKinds.Macro, RiskSeverity.Block, "an ActiveX control");
                else if (ExecutableSniffer.HasExecutableExtension(name))
                    Add(RiskKinds.ExecutableAttachment, RiskSeverity.Block, $"an embedded file named '{UsageFileName.Clean(name)}' that looks like a program or script");
                else if (name.Contains("/embeddings/", StringComparison.OrdinalIgnoreCase))
                    Add(RiskKinds.Macro, RiskSeverity.Info, "an embedded object");
                else if (name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase) && entry.Length <= MaxRelationshipBytes && ++relationshipFiles <= MaxRelationshipFiles)
                    InspectRelationships(entry, Add);
            }
        }
        finally
        {
            package.Position = 0;
        }
        return risks;
    }

    private static void InspectRelationships(ZipArchiveEntry entry, Action<string, RiskSeverity, string> add)
    {
        try
        {
            using var reader = XmlReader.Create(entry.Open(),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000 });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship") continue;
                if (!string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)) continue;
                var type = reader.GetAttribute("Type") ?? "";
                if (type.EndsWith("/attachedTemplate", StringComparison.OrdinalIgnoreCase))
                    add(RiskKinds.Macro, RiskSeverity.Block, "a template loaded from another location");
                else if (type.EndsWith("/oleObject", StringComparison.OrdinalIgnoreCase) || type.EndsWith("/frame", StringComparison.OrdinalIgnoreCase))
                    add(RiskKinds.Macro, RiskSeverity.Block, "an object linked from another location");
                else if (type.EndsWith("/externalLink", StringComparison.OrdinalIgnoreCase))
                    add(RiskKinds.Macro, RiskSeverity.Info, "a link to another workbook");
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
        {
            // A relationships part that cannot be read is not evidence of anything; the converter will meet it too.
        }
    }
}

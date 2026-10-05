using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.AcroForms;
using UglyToad.PdfPig.AcroForms.Fields;
using UglyToad.PdfPig.Tokens;

namespace LayaSample.Api.Services.Documents.Pdf;

/// <summary>Reads machine-readable data out of a PDF: form values, XFA datasets and embedded files.</summary>
public static class PdfStructuredData
{
    public static bool HasValue(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => !string.IsNullOrWhiteSpace(s) && s != "Off",
        System.Collections.IEnumerable e => e.Cast<object?>().Any(HasValue),
        _ => !string.IsNullOrWhiteSpace(value.ToString())
    };

    /// <summary>
    /// The value a person entered or selected: a string, a bool (checkbox), a list of strings (multi-select), or null.
    /// PdfPig's own <c>GetFieldValue</c> only covers text and checkbox fields and wraps the value in a name/value pair.
    /// </summary>
    public static object? FieldValue(AcroFieldBase field) => field switch
    {
        AcroTextField text => text.Value,
        AcroCheckboxField checkbox => checkbox.IsChecked,
        AcroCheckboxesField group => group.Children.OfType<AcroCheckboxField>().Where(c => c.IsChecked).Select(c => c.CurrentValue.Data).ToList(),
        AcroRadioButtonField radio => radio.IsSelected ? radio.CurrentValue.Data : null,
        AcroRadioButtonsField group => group.Children.OfType<AcroRadioButtonField>().FirstOrDefault(r => r.IsSelected)?.CurrentValue.Data,
        AcroComboBoxField combo => Selected(combo.SelectedOptions),
        AcroListBoxField list => Selected(list.SelectedOptions),
        _ => null
    };

    private static object? Selected(IReadOnlyList<string> options) => options.Count switch
    {
        0 => null,
        1 => options[0],
        _ => options.ToList()
    };

    /// <summary>AcroForm field values as a JSON object keyed by field name, or null when there are no fields.</summary>
    public static string? ReadFormValuesJson(PdfDocument pdf)
    {
        if (!pdf.TryGetForm(out var form) || form is null) return null;

        var values = new Dictionary<string, object?>();
        foreach (var field in form.GetFields())
        {
            if (field is AcroSignatureField || field.FieldType == AcroFieldType.PushButton) continue;

            var name = field.Information.PartialName ?? field.Information.AlternateName ?? $"field{values.Count + 1}";
            var key = name;
            for (var i = 2; values.ContainsKey(key); i++) key = $"{name}#{i}";
            values[key] = FieldValue(field);
        }
        return values.Count == 0 ? null : JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The XFA <c>datasets</c> packet (the form's filled data) as XML, or null.</summary>
    public static string? ReadXfaDatasets(PdfDocument pdf)
    {
        try
        {
            var xfa = PdfTokens.Resolve(pdf, PdfTokens.AcroForm(pdf)?.Data.GetValueOrDefault("XFA"));
            switch (xfa)
            {
                // Array form: [(name) stream (name) stream ...] — read the datasets packet directly.
                case ArrayToken packets:
                    for (var i = 0; i + 1 < packets.Data.Count; i += 2)
                    {
                        if (PdfTokens.Text(packets.Data[i]) != "datasets") continue;
                        return PdfTokens.Resolve(pdf, packets.Data[i + 1]) is StreamToken s && PdfTokens.Decode(s) is { } bytes
                            ? ParseXml(Encoding.UTF8.GetString(bytes)).ToString()
                            : null;
                    }
                    return null;

                // Single stream: the whole XDP document.
                case StreamToken stream when PdfTokens.Decode(stream) is { } xdp:
                    return ParseXml(Encoding.UTF8.GetString(xdp)).Descendants().FirstOrDefault(e => e.Name.LocalName == "datasets")?.ToString();

                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException)
        {
            return null;
        }
    }

    public static IReadOnlyList<(string Name, byte[] Bytes)> ReadAttachments(PdfDocument pdf) =>
        pdf.Advanced.TryGetEmbeddedFiles(out var files) && files is not null
            ? files.Select(f => (f.Name, f.Memory.ToArray())).ToList()
            : [];

    /// <summary>Parses untrusted XML with DTDs disabled (no entity expansion / external resolution).</summary>
    public static XElement ParseXml(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml.TrimStart('﻿')),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XElement.Load(reader);
    }
}

using System.Globalization;
using System.Text;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;

namespace Intelimensa.Accounts.Importing;

/// <summary>
/// Parses a CSV of manufactured BCI units into <see cref="BciDevice"/> rows. Header-driven
/// (column order doesn't matter, names are case-insensitive): <c>serial_number</c>,
/// <c>device_type</c>, <c>produced_at</c> (yyyy-MM-dd), <c>firmware_version</c>. All-or-nothing --
/// the caller only persists <see cref="Result.Devices"/> when <see cref="Result.Errors"/> is empty.
/// Handles RFC 4180 quoting (embedded commas, quotes, newlines) and skips blank lines.
/// </summary>
public static class BciDeviceCsvParser
{
    public const int MaxRows = 5000;

    private static readonly string[] RequiredColumns =
        ["serial_number", "device_type", "produced_at", "firmware_version"];

    public record RowError(int Line, string Message);

    public record Result(List<BciDevice> Devices, List<RowError> Errors);

    public static Result Parse(TextReader reader)
    {
        var devices = new List<BciDevice>();
        var errors = new List<RowError>();

        var records = ReadRecords(reader).GetEnumerator();
        if (!records.MoveNext())
        {
            errors.Add(new RowError(1, "The file is empty."));
            return new Result(devices, errors);
        }

        var header = records.Current.Fields.Select(f => f.Trim('﻿', ' ', '\t').ToLowerInvariant()).ToList();
        var missing = RequiredColumns.Where(c => !header.Contains(c)).ToList();
        if (missing.Count > 0)
        {
            errors.Add(new RowError(records.Current.Line,
                $"Missing required column(s): {string.Join(", ", missing)}. Expected header: {string.Join(",", RequiredColumns)}."));
            return new Result(devices, errors);
        }

        var serialIdx = header.IndexOf("serial_number");
        var typeIdx = header.IndexOf("device_type");
        var producedIdx = header.IndexOf("produced_at");
        var firmwareIdx = header.IndexOf("firmware_version");

        var seenSerials = new Dictionary<string, int>(StringComparer.Ordinal);
        var rowCount = 0;

        while (records.MoveNext())
        {
            var (line, fields) = records.Current;
            if (++rowCount > MaxRows)
            {
                errors.Add(new RowError(line, $"Too many rows -- the limit is {MaxRows} per import."));
                break;
            }

            string Field(int idx) => idx < fields.Count ? fields[idx].Trim() : string.Empty;
            var serial = Field(serialIdx);
            if (SerialNumber.TryNormalize(serial, out var canonicalSerial))
                serial = canonicalSerial; // serials in the current format are stored dashless
            var type = Field(typeIdx);
            var producedRaw = Field(producedIdx);
            var firmware = Field(firmwareIdx);

            var rowErrors = errors.Count;
            if (serial.Length is 0 or > 100)
                errors.Add(new RowError(line, "serial_number is required (max 100 characters)."));
            if (type.Length is 0 or > 100)
                errors.Add(new RowError(line, "device_type is required (max 100 characters)."));
            if (firmware.Length is 0 or > 50)
                errors.Add(new RowError(line, "firmware_version is required (max 50 characters)."));
            if (!DateOnly.TryParseExact(producedRaw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var produced))
                errors.Add(new RowError(line, $"produced_at '{producedRaw}' is not a valid yyyy-MM-dd date."));

            if (serial.Length > 0)
            {
                if (seenSerials.TryGetValue(serial, out var firstLine))
                    errors.Add(new RowError(line, $"Duplicate serial '{serial}' (first seen on line {firstLine})."));
                else
                    seenSerials[serial] = line;
            }

            if (errors.Count == rowErrors)
            {
                devices.Add(new BciDevice
                {
                    SerialNumber = serial,
                    DeviceType = type,
                    ProducedAt = produced,
                    CurrentFirmwareVersion = firmware,
                });
            }
        }

        return new Result(devices, errors);
    }

    private record Record(int Line, List<string> Fields);

    /// <summary>Yields one record per logical CSV row, tagged with the physical line it started on.</summary>
    private static IEnumerable<Record> ReadRecords(TextReader reader)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var recordLine = 1;
        var recordHasContent = false;

        int c;
        while ((c = reader.Read()) != -1)
        {
            var ch = (char)c;

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                    else inQuotes = false;
                }
                else
                {
                    if (ch == '\n') line++;
                    field.Append(ch);
                }
                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    recordHasContent = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    recordHasContent = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    if (recordHasContent || field.Length > 0)
                    {
                        fields.Add(field.ToString());
                        yield return new Record(recordLine, fields);
                    }
                    fields = [];
                    field.Clear();
                    recordHasContent = false;
                    line++;
                    recordLine = line;
                    break;
                default:
                    field.Append(ch);
                    recordHasContent = true;
                    break;
            }
        }

        if (recordHasContent || field.Length > 0)
        {
            fields.Add(field.ToString());
            yield return new Record(recordLine, fields);
        }
    }
}

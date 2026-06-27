using System;
using System.Data.Common;
using System.Text;

namespace Socigy.OpenSource.DB.Core.Diagnostics
{
#nullable enable
    /// <summary>
    /// Renders a command's parameter collection for spans and logs. By default only parameter names and
    /// DB types are emitted; raw values are included only when <see cref="SocigyDbDiagnosticsOptions.CaptureParameterValues"/>
    /// is enabled, honoring the redaction hook and length cap. Reflection-free and AOT/trim-safe.
    /// </summary>
    internal static class ParameterSerializer
    {
        public static string Serialize(DbParameterCollection? parameters, SocigyDbDiagnosticsOptions options)
        {
            if (parameters == null || parameters.Count == 0)
                return "(none)";

            var sb = new StringBuilder();
            for (int i = 0; i < parameters.Count; i++)
            {
                DbParameter p = parameters[i];
                if (i > 0) sb.Append(", ");
                sb.Append(p.ParameterName).Append('=');

                if (!options.CaptureParameterValues)
                {
                    sb.Append('<').Append(p.DbType).Append('>');
                    continue;
                }

                // The length cap bounds emission size and must apply to a custom redaction hook too — otherwise
                // a hook that echoes the value (or returns a long token) bypasses the cap and can bloat every span.
                // Diagnostics must NEVER throw into the query path: a throwing redaction hook, or a value whose
                // ToString() throws, would otherwise crash an already-successful command (or, on the failure path,
                // mask the original DB exception). Substitute a placeholder instead.
                string? rendered;
                try
                {
                    rendered = options.RedactParameter != null
                        ? Truncate(options.RedactParameter(p.ParameterName, p.Value), options.MaxParameterValueLength)
                        : RenderValue(p.Value, options.MaxParameterValueLength);
                }
                catch
                {
                    rendered = "<unrenderable>";
                }

                sb.Append(rendered ?? "NULL");
            }
            return sb.ToString();
        }

        private static string RenderValue(object? value, int maxLength)
        {
            if (value == null || value is DBNull)
                return "NULL";

            string s;
            switch (value)
            {
                case byte[] bytes:
                    s = "0x[" + bytes.Length + " bytes]";
                    break;
                // Round-trip format so a 'timestamp' vs 'timestamptz' / Kind / offset is visible when debugging
                // timezone issues (the invariant default drops Kind, offset, and sub-second precision).
                case DateTime dt:
                    s = dt.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case DateTimeOffset dto:
                    s = dto.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
                    break;
                // string before IEnumerable (string is IEnumerable<char>).
                case string str:
                    s = str;
                    break;
                // Array / List (= ANY(@p) parameters) — render bounded contents, not "System.Int32[]".
                case System.Collections.IEnumerable seq:
                    s = RenderCollection(seq);
                    break;
                default:
                    s = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                    break;
            }

            return Truncate(s, maxLength);
        }

        private static string RenderCollection(System.Collections.IEnumerable seq)
        {
            var sb = new StringBuilder("[");
            int count = 0;
            foreach (var item in seq)
            {
                if (count >= 20) { sb.Append(", …"); break; }   // bound the element count; Truncate bounds total length
                if (count > 0) sb.Append(", ");
                sb.Append(item == null || item is DBNull ? "NULL"
                    : item is byte[] b ? "0x[" + b.Length + " bytes]"
                    : Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                count++;
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string? Truncate(string? s, int maxLength)
        {
            if (s != null && maxLength > 0 && s.Length > maxLength)
                return s.Substring(0, maxLength) + "…(truncated)";

            return s;
        }
    }
#nullable disable
}

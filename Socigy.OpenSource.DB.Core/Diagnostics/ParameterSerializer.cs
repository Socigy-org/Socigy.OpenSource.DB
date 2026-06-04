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

                string? rendered = options.RedactParameter != null
                    ? options.RedactParameter(p.ParameterName, p.Value)
                    : RenderValue(p.Value, options.MaxParameterValueLength);

                sb.Append(rendered ?? "NULL");
            }
            return sb.ToString();
        }

        private static string RenderValue(object? value, int maxLength)
        {
            if (value == null || value is DBNull)
                return "NULL";

            string s = value is byte[] bytes
                ? "0x[" + bytes.Length + " bytes]"
                : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

            if (maxLength > 0 && s.Length > maxLength)
                return s.Substring(0, maxLength) + "…(truncated)";

            return s;
        }
    }
#nullable disable
}

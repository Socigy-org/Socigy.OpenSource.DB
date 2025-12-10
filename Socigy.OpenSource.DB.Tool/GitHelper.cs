using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Socigy.OpenSource.DB.Tool
{
    public static class GitHelper
    {
        public static string? GetGitSignature(string projectDir)
        {
            string? name = GetGitConfigValue(projectDir, "user.name");
            string? email = GetGitConfigValue(projectDir, "user.email");

            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(email))
                return null;

            string safeName = string.IsNullOrEmpty(name) ? Environment.MachineName : name;
            string safeEmail = string.IsNullOrEmpty(email) ? string.Empty : $" - {email}";

            return $"{safeName}{safeEmail}";
        }

        private static string? GetGitConfigValue(string projectDir, string configKey)
        {
            if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
                projectDir = Environment.CurrentDirectory;

            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"config {configKey}",
                    WorkingDirectory = projectDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(processInfo))
                {
                    if (process == null) return null;

                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
                }
            }
            catch
            {
                return null;
            }
        }
    }
}

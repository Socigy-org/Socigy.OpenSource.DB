namespace Socigy.OpenSource.DB.Tool
{
    internal static class Logger
    {
        public const string DefaultOwner = "Socigy.OpenSource.DB.Tool";

        public static void Log(string message, string? owner = DefaultOwner, string? colorCode = null)
        {
            owner ??= DefaultOwner;

            Console.WriteLine($"{colorCode}[{owner}] {message}\e[0m");
        }

        public static void Warning(string message, string? owner = null)
        {
            Log(message, owner, "\e[0;33m");
        }

        public static void Error(string message, string? owner = null)
        {
            Log(message, owner, "\e[0;31m");
        }
    }
}

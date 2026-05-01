using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Core.Convertors;

namespace Example.Auth.DB
{
    public class LowerCaseConvertor : IDbValueConvertor<string>
    {
        public object? ConvertToDbValue(string? value) => value?.ToLowerInvariant();
        public string? ConvertFromDbValue(object? dbValue) => dbValue?.ToString();
    }

    [Table("user_login")]
    public partial class UserLogin
    {
        [PrimaryKey, Default]
        public Guid Id { get; set; }

        [ValueConvertor(typeof(LowerCaseConvertor))]
        public string Username { get; set; } = "";

        public string? PasswordHash { get; set; }
    }
}
